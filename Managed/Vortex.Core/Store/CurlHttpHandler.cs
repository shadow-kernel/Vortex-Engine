using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Editor.Core.Assets.Store
{
    /// <summary>
    /// HTTPS through curl, for the hosts .NET cannot reach on macOS (#403): .NET's SslStream there runs on Apple's
    /// SecureTransport, which has no TLS 1.3, and api.stability.ai speaks nothing else. /usr/bin/curl (LibreSSL) does
    /// TLS 1.3 and verifies the server certificate against the system's CAs; ~/.curlrc is never read, so nothing can turn
    /// that off. Nothing secret reaches the process list or a log: every request header — the API key among them — goes
    /// in through curl's stdin (<c>-H @-</c>), the body through a file in an owner-only temp folder, and the response
    /// comes back in files there. Failures read like .NET's own (a refused handshake is an HttpRequestException around
    /// an AuthenticationException), so <see cref="StoreHttp.Describe"/> explains both alike.
    /// (The alternative, .NET's opt-in Network.framework TLS, is a process-wide switch, and in .NET 10.0.12 it breaks
    /// every second resumed TLS 1.2 connection of every other client in the process.)
    /// </summary>
    public sealed class CurlHttpHandler : HttpMessageHandler
    {
        public const string DefaultPath = "/usr/bin/curl";

        public string CurlPath { get; set; } = DefaultPath;
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public TimeSpan MaxTime { get; set; } = TimeSpan.FromMinutes(30);
        /// <summary>The protocols curl may speak (its --proto); tests open it to plain http on a local listener.</summary>
        internal string Protocols { get; set; } = "=https";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request?.RequestUri == null) throw new ArgumentException("The request has no URL.");
            var dir = Directory.CreateTempSubdirectory("vortex-curl-");   // 0700: only this user reads the body and the answer
            try
            {
                string bodyFile = null;
                if (request.Content != null)
                {
                    bodyFile = Path.Combine(dir.FullName, "body");
                    await File.WriteAllBytesAsync(bodyFile, await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                }
                string headerFile = Path.Combine(dir.FullName, "headers"), outFile = Path.Combine(dir.FullName, "response");

                var psi = new ProcessStartInfo(CurlPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false),
                };
                foreach (var a in Arguments(request, bodyFile, headerFile, outFile)) psi.ArgumentList.Add(a);
                // macOS's curl carries two TLS backends; its default (LibreSSL) is the one with TLS 1.3 — SecureTransport has none
                psi.Environment.Remove("CURL_SSL_BACKEND");

                using (var p = new Process { StartInfo = psi })
                {
                    try { p.Start(); }
                    catch (Exception ex) { throw new HttpRequestException("curl could not be started (" + CurlPath + "), see inner exception.", ex); }
                    using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                    {
                        var stdout = p.StandardOutput.ReadToEndAsync();
                        var stderr = p.StandardError.ReadToEndAsync();
                        try
                        {
                            await p.StandardInput.WriteAsync(HeaderLines(request)).ConfigureAwait(false);
                            p.StandardInput.Close();
                        }
                        catch (IOException) { }   // curl gave up before reading its headers: its exit code says why
                        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                        string written = (await stdout.ConfigureAwait(false)).Trim(), errors = await stderr.ConfigureAwait(false);
                        if (p.ExitCode != 0) throw Failure(p.ExitCode, errors);
                        if (!int.TryParse(written, NumberStyles.Integer, CultureInfo.InvariantCulture, out int status) || status < 100)
                            throw new HttpRequestException("curl reported no HTTP status (" + written + ").");

                        var response = new HttpResponseMessage((HttpStatusCode)status)
                        {
                            RequestMessage = request,
                            Content = new ByteArrayContent(File.Exists(outFile) ? await File.ReadAllBytesAsync(outFile, CancellationToken.None).ConfigureAwait(false) : new byte[0]),
                        };
                        if (File.Exists(headerFile)) ApplyHeaders(response, File.ReadAllText(headerFile));
                        return response;
                    }
                }
            }
            finally
            {
                try { dir.Delete(true); } catch { }
            }
        }

        /// <summary>curl's command line for a request — no header and no body on it, so nothing secret: those go through
        /// stdin and a file.</summary>
        internal List<string> Arguments(HttpRequestMessage request, string bodyFile, string headerFile, string outFile)
        {
            string Seconds(TimeSpan t) => Math.Max(1, (int)Math.Ceiling(t.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            var a = new List<string>
            {
                "--disable",                                        // first: never read ~/.curlrc (it could say --insecure)
                "--silent", "--show-error",
                "--proto", Protocols, "--proto-redir", Protocols,
                "--location", "--max-redirs", "8",                  // as StoreHttp's own client
                "--connect-timeout", Seconds(ConnectTimeout),
                "--max-time", Seconds(MaxTime),
                "--header", "@-",                                   // every request header from stdin (HeaderLines)
                "--dump-header", headerFile,
                "--output", outFile,
                "--write-out", "%{http_code}",
            };
            string method = request.Method.Method;
            if (bodyFile != null) { a.Add("--data-binary"); a.Add("@" + bodyFile); }
            if (bodyFile != null ? method != "POST" : method != "GET") { a.Add("--request"); a.Add(method); }
            a.Add(request.RequestUri.AbsoluteUri);
            return a;
        }

        /// <summary>The request's headers as curl reads them with <c>-H @-</c>: one "Name: value" line each, content headers
        /// included; line breaks inside a value are dropped so no value can add a header of its own.</summary>
        internal static string HeaderLines(HttpRequestMessage request)
        {
            var sb = new StringBuilder();
            void Add(HttpHeaders headers)
            {
                foreach (var h in headers.NonValidated)
                {
                    // curl sets these from the body it sends; "Expect" is switched off below
                    if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                        || h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Expect", StringComparison.OrdinalIgnoreCase)) continue;
                    string value = h.Value.ToString().Replace("\r", " ").Replace("\n", " ");
                    sb.Append(h.Key).Append(": ").Append(value).Append('\n');
                }
            }
            Add(request.Headers);
            if (request.Content != null) Add(request.Content.Headers);
            sb.Append("Expect:\n");   // no 100-continue round trip
            return sb.ToString();
        }

        /// <summary>The headers of the final response in curl's --dump-header file (it holds every response of a redirect
        /// chain, each block led by its status line).</summary>
        private static void ApplyHeaders(HttpResponseMessage response, string dump)
        {
            var lines = dump.Replace("\r\n", "\n").Split('\n');
            int start = 0;
            for (int i = 0; i < lines.Length; i++) if (lines[i].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)) start = i + 1;
            for (int i = start; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string name = lines[i].Substring(0, colon).Trim(), value = lines[i].Substring(colon + 1).Trim();
                // the body is already whole and decoded as curl wrote it: its length is the content's own
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        /// <summary>A failed curl run as .NET would have reported it: a TLS failure is an HttpRequestException around an
        /// AuthenticationException, a time-out one around a TimeoutException, anything else one around an IOException —
        /// with curl's own words (exit code + message) as the cause.</summary>
        internal static Exception Failure(int exitCode, string stderr)
        {
            string detail = (stderr ?? "").Trim();
            string prefix = "curl: (" + exitCode.ToString(CultureInfo.InvariantCulture) + ")";
            if (detail.StartsWith(prefix, StringComparison.Ordinal)) detail = detail.Substring(prefix.Length).Trim();
            string cause = "curl exit " + exitCode.ToString(CultureInfo.InvariantCulture) + (detail.Length > 0 ? ": " + detail : "");
            switch (exitCode)
            {
                // the TLS handshake, the server's certificate, the CA store
                case 35: case 51: case 53: case 54: case 58: case 59: case 60: case 64: case 66: case 77: case 80: case 82: case 83: case 90: case 91:
                    return new HttpRequestException("The SSL connection could not be established, see inner exception.", new AuthenticationException(cause));
                case 28:
                    return new HttpRequestException("The request timed out, see inner exception.", new TimeoutException(cause));
                default:   // 5/6 name resolution, 7 connect, 52/55/56 the transfer broke …
                    return new HttpRequestException("The request failed, see inner exception.", new IOException(cause));
            }
        }
    }
}
