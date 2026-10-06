using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Editor.Core.Claude.Terminal
{
    /// <summary>
    /// A program running in a pseudo-terminal, so it behaves as in a terminal window (colours, a full-screen interface,
    /// window size): what it writes arrives through <see cref="Output"/>, keys go in with <see cref="Write(string)"/>,
    /// and <see cref="Resize"/> tells it the new size. macOS and Linux: <c>posix_openpt</c> plus <c>posix_spawn</c> in a new
    /// session, so the terminal becomes the program's controlling terminal (resizes reach it as SIGWINCH). Windows: the
    /// ConPTY pseudo console.
    /// </summary>
    public abstract class Pty : IDisposable
    {
        /// <summary>Bytes the program wrote (on a background thread).</summary>
        public event Action<byte[], int> Output;
        /// <summary>The program ended (on a background thread); the argument is its exit code.</summary>
        public event Action<int> Exited;

        public int ProcessId { get; protected set; }
        public bool HasExited { get; protected set; }

        public abstract void Write(byte[] data);
        public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));
        public abstract void Resize(int cols, int rows);
        public abstract void Kill();
        public abstract void Dispose();

        protected void RaiseOutput(byte[] buffer, int count) { try { Output?.Invoke(buffer, count); } catch { } }
        protected void RaiseExited(int code) { HasExited = true; try { Exited?.Invoke(code); } catch { } }

        /// <summary>Start <paramref name="file"/> with <paramref name="args"/> in <paramref name="cwd"/>.</summary>
        public static Pty Start(string file, IReadOnlyList<string> args, string cwd, IDictionary<string, string> env, int cols, int rows)
        {
            if (OperatingSystem.IsWindows()) return new WindowsPty(file, args, cwd, env, cols, rows);
            return new UnixPty(file, args, cwd, env, cols, rows);
        }

        /// <summary>The environment for the program: the editor's own, plus what a terminal sets.</summary>
        public static Dictionary<string, string> TerminalEnvironment(IDictionary<string, string> extra = null)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                env[(string)e.Key] = (string)e.Value;
            env["TERM"] = "xterm-256color";
            env["COLORTERM"] = "truecolor";
            env["TERM_PROGRAM"] = "VortexEditor";
            if (!OperatingSystem.IsWindows() && (!env.TryGetValue("LANG", out var lang) || string.IsNullOrEmpty(lang))) env["LANG"] = "en_US.UTF-8";
            if (extra != null) foreach (var kv in extra) env[kv.Key] = kv.Value;
            return env;
        }
    }

    // ====================================================================== macOS / Linux

    internal sealed class UnixPty : Pty
    {
        private readonly int _master;
        private readonly Thread _reader, _waiter;
        private volatile bool _disposed;

        public UnixPty(string file, IReadOnlyList<string> args, string cwd, IDictionary<string, string> env, int cols, int rows)
        {
            bool mac = OperatingSystem.IsMacOS();
            int O_RDWR = 2, O_NOCTTY = mac ? 0x20000 : 0x100;
            _master = posix_openpt(O_RDWR | O_NOCTTY);
            if (_master < 0) throw new IOException("posix_openpt failed (" + Marshal.GetLastWin32Error() + ")");
            if (grantpt(_master) != 0 || unlockpt(_master) != 0) { close(_master); throw new IOException("grantpt/unlockpt failed"); }
            string slave = Marshal.PtrToStringAnsi(ptsname(_master)) ?? throw new IOException("ptsname failed");

            // the size must be set before the program starts (it reads it once at start): open the slave here for that
            int slaveFd = open(slave, O_RDWR | O_NOCTTY);
            if (slaveFd >= 0) { SetSize(slaveFd, cols, rows); }

            IntPtr fa = Marshal.AllocHGlobal(512), attr = Marshal.AllocHGlobal(512);
            var argv = Strings(new[] { file }.Concat(args).ToArray());
            var envp = Strings(env.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            try
            {
                posix_spawn_file_actions_init(fa);
                if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd))
                {
                    // macOS 10.15+ / glibc 2.29+: change directory in the child
                    try { posix_spawn_file_actions_addchdir_np(fa, cwd); } catch (EntryPointNotFoundException) { }
                }
                posix_spawn_file_actions_addopen(fa, 0, slave, O_RDWR, 0);
                posix_spawn_file_actions_adddup2(fa, 0, 1);
                posix_spawn_file_actions_adddup2(fa, 0, 2);
                posix_spawn_file_actions_addclose(fa, _master);
                if (slaveFd >= 0) posix_spawn_file_actions_addclose(fa, slaveFd);
                posix_spawnattr_init(attr);
                // a new session: the terminal opened as fd 0 becomes its controlling terminal; on macOS close every other
                // descriptor the editor has open
                short flags = mac ? (short)(0x0400 | 0x4000) : (short)0x80;   // POSIX_SPAWN_SETSID (| POSIX_SPAWN_CLOEXEC_DEFAULT)
                posix_spawnattr_setflags(attr, flags);
                int rc = posix_spawn(out int pid, file, fa, attr, argv, envp);
                if (rc != 0) throw new Win32Exception(rc, "posix_spawn " + file + " failed");
                ProcessId = pid;
            }
            catch
            {
                close(_master);
                throw;
            }
            finally
            {
                posix_spawn_file_actions_destroy(fa);
                posix_spawnattr_destroy(attr);
                Marshal.FreeHGlobal(fa);
                Marshal.FreeHGlobal(attr);
                foreach (var p in argv) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
                foreach (var p in envp) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
                if (slaveFd >= 0) close(slaveFd);
            }

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty reader" };
            _reader.Start();
            _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "pty waiter" };
            _waiter.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[16384];
            while (!_disposed)
            {
                long n = (long)read(_master, buf, (IntPtr)buf.Length);
                if (n > 0) { RaiseOutput(buf, (int)n); continue; }
                if (n < 0 && Marshal.GetLastWin32Error() == 4) continue;   // EINTR
                break;                                                     // EOF / EIO: the program closed the terminal
            }
        }

        private void WaitLoop()
        {
            int r;
            int status;
            do r = waitpid(ProcessId, out status, 0); while (r < 0 && Marshal.GetLastWin32Error() == 4);
            int code = (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);
            Thread.Sleep(50);   // let the reader drain the last output
            RaiseExited(code);
        }

        public override void Write(byte[] data)
        {
            if (_disposed || HasExited) return;
            int off = 0;
            while (off < data.Length)
            {
                var chunk = off == 0 ? data : data.Skip(off).ToArray();
                long n = (long)write(_master, chunk, (IntPtr)chunk.Length);
                if (n < 0) { if (Marshal.GetLastWin32Error() == 4) continue; return; }
                off += (int)n;
            }
        }

        public override void Resize(int cols, int rows)
        {
            if (!_disposed) SetSize(_master, cols, rows);
        }

        public override void Kill()
        {
            if (!HasExited && ProcessId > 0) kill(ProcessId, 1);   // SIGHUP, as when a terminal window closes
        }

        public override void Dispose()
        {
            if (_disposed) return;
            Kill();
            _disposed = true;
            close(_master);
        }

        private static IntPtr[] Strings(string[] s)
        {
            var a = new IntPtr[s.Length + 1];
            for (int i = 0; i < s.Length; i++) a[i] = Marshal.StringToCoTaskMemUTF8(s[i]);
            return a;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinSize { public ushort Row, Col, X, Y; }

        private static void SetSize(int fd, int cols, int rows)
        {
            var ws = new WinSize { Row = (ushort)Math.Clamp(rows, 1, 1000), Col = (ushort)Math.Clamp(cols, 1, 1000) };
            ulong req = OperatingSystem.IsMacOS() ? 0x80087467UL : 0x5414UL;   // TIOCSWINSZ
            // ioctl is variadic, and Apple's arm64 ABI passes variadic arguments on the stack: fill the eight argument
            // registers so the pointer lands there
            if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                ioctl_arm64(fd, req, 0, 0, 0, 0, 0, 0, ref ws);
            else ioctl(fd, req, ref ws);
        }

        [DllImport("libc", SetLastError = true)] private static extern int posix_openpt(int flags);
        [DllImport("libc", SetLastError = true)] private static extern int grantpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int unlockpt(int fd);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr ptsname(int fd);
        [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", SetLastError = true)] private static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr read(int fd, byte[] buf, IntPtr count);
        [DllImport("libc", SetLastError = true)] private static extern IntPtr write(int fd, byte[] buf, IntPtr count);
        [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
        [DllImport("libc", SetLastError = true)] private static extern int kill(int pid, int sig);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int ioctl(int fd, ulong req, ref WinSize ws);
        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)] private static extern int ioctl_arm64(int fd, ulong req, long a2, long a3, long a4, long a5, long a6, long a7, ref WinSize ws);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawn_file_actions_init(IntPtr fa);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawn_file_actions_destroy(IntPtr fa);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawn_file_actions_addopen(IntPtr fa, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int oflag, int mode);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawn_file_actions_adddup2(IntPtr fa, int fd, int newfd);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawn_file_actions_addclose(IntPtr fa, int fd);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawn_file_actions_addchdir_np(IntPtr fa, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawnattr_init(IntPtr attr);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawnattr_destroy(IntPtr attr);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawnattr_setflags(IntPtr attr, short flags);
        [DllImport("libc", SetLastError = true)] private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr fa, IntPtr attr, IntPtr[] argv, IntPtr[] envp);
    }

    // ====================================================================== Windows (ConPTY)

    internal sealed class WindowsPty : Pty
    {
        private IntPtr _console;
        private readonly SafeFileHandle _inputWrite, _outputRead;
        private readonly FileStream _input;
        private readonly IntPtr _process, _thread;
        private readonly Thread _reader, _waiter;
        private volatile bool _disposed;

        public WindowsPty(string file, IReadOnlyList<string> args, string cwd, IDictionary<string, string> env, int cols, int rows)
        {
            if (!CreatePipe(out var inputRead, out _inputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            if (!CreatePipe(out _outputRead, out var outputWrite, IntPtr.Zero, 0)) throw new Win32Exception();
            int hr = CreatePseudoConsole(new Coord { X = (short)cols, Y = (short)rows }, inputRead, outputWrite, 0, out _console);
            inputRead.Dispose();
            outputWrite.Dispose();
            if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole failed (Windows 10 1809 or later is needed)");

            var si = new StartupInfoEx();
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            si.lpAttributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)0x00020016 /* PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE */, _console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();

            string commandLine = Quote(file) + string.Concat(args.Select(a => " " + Quote(a)));
            var envBlock = new StringBuilder();
            foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) envBlock.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
            envBlock.Append('\0');
            const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000, CREATE_UNICODE_ENVIRONMENT = 0x00000400;
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                                envBlock.ToString(), string.IsNullOrEmpty(cwd) ? null : cwd, ref si, out var pi))
            {
                int err = Marshal.GetLastWin32Error();
                DeleteProcThreadAttributeList(si.lpAttributeList);
                Marshal.FreeHGlobal(si.lpAttributeList);
                ClosePseudoConsole(_console);
                throw new Win32Exception(err, "CreateProcess " + file + " failed");
            }
            DeleteProcThreadAttributeList(si.lpAttributeList);
            Marshal.FreeHGlobal(si.lpAttributeList);
            _process = pi.hProcess;
            _thread = pi.hThread;
            ProcessId = pi.dwProcessId;
            _input = new FileStream(_inputWrite, FileAccess.Write, 1);

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = "conpty reader" };
            _reader.Start();
            _waiter = new Thread(WaitLoop) { IsBackground = true, Name = "conpty waiter" };
            _waiter.Start();
        }

        private void ReadLoop()
        {
            var buf = new byte[16384];
            using var output = new FileStream(_outputRead, FileAccess.Read, 1);
            try
            {
                int n;
                while (!_disposed && (n = output.Read(buf, 0, buf.Length)) > 0) RaiseOutput(buf, n);
            }
            catch { }
        }

        private void WaitLoop()
        {
            WaitForSingleObject(_process, uint.MaxValue);
            GetExitCodeProcess(_process, out uint code);
            Thread.Sleep(50);
            RaiseExited((int)code);
            // closing the pseudo console ends the reader's pipe
            if (!_disposed) { try { ClosePseudoConsole(_console); } catch { } _console = IntPtr.Zero; }
        }

        public override void Write(byte[] data)
        {
            if (_disposed || HasExited) return;
            try { _input.Write(data, 0, data.Length); _input.Flush(); } catch { }
        }

        public override void Resize(int cols, int rows)
        {
            if (!_disposed && _console != IntPtr.Zero) ResizePseudoConsole(_console, new Coord { X = (short)Math.Clamp(cols, 1, 1000), Y = (short)Math.Clamp(rows, 1, 1000) });
        }

        public override void Kill()
        {
            if (!HasExited) TerminateProcess(_process, 1);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Kill();
            try { _input.Dispose(); } catch { }
            if (_console != IntPtr.Zero) { try { ClosePseudoConsole(_console); } catch { } _console = IntPtr.Zero; }
            CloseHandle(_thread);
            CloseHandle(_process);
        }

        private static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1).Append('"');
                else sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            return sb.Append('\\', slashes * 2).Append('"').ToString();
        }

        [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb; public string lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessW(string app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags,
                                                  string environment, string cwd, ref StartupInfoEx si, out ProcessInformation pi);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }
}
