using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Editor.Core.Serialization;

namespace Editor.Core.Assets.Library
{
    /// <summary>A third-party asset in a project, as its .vmeta records it.</summary>
    public sealed class LicensedAsset
    {
        public string RelPath;
        public string Name;
        public string License;
        public string Author;
        public string Source;
        public string SourceUrl;
    }

    public sealed class LicenseAuditReport
    {
        public readonly List<LicensedAsset> Assets = new List<LicensedAsset>();
        /// <summary>One entry per source item (a model and its textures share one credit).</summary>
        public readonly List<LicensedAsset> Credits = new List<LicensedAsset>();
        public readonly List<LicensedAsset> NonCommercial = new List<LicensedAsset>();
        public readonly List<LicensedAsset> NoDerivatives = new List<LicensedAsset>();
        public readonly List<LicensedAsset> ShareAlike = new List<LicensedAsset>();
        public readonly List<LicensedAsset> Unknown = new List<LicensedAsset>();
        public bool HasWarnings => NonCommercial.Count + NoDerivatives.Count + ShareAlike.Count + Unknown.Count > 0;
    }

    /// <summary>
    /// License check for game exports (#77): reads the license / author / source the asset library wrote into each
    /// project .vmeta, generates <c>CREDITS.md</c> (every attribution-required asset with author, title, source and
    /// license — CC-BY compliance out of the box) and lists what needs a decision before shipping: NonCommercial,
    /// NoDerivatives and ShareAlike assets and unknown licenses. Framework-free (both editors).
    /// </summary>
    public static class LicenseAudit
    {
        public static bool NeedsCredit(string id) => !string.IsNullOrEmpty(id) && (id.StartsWith("CC-BY", StringComparison.OrdinalIgnoreCase) || id.StartsWith("Sampling", StringComparison.OrdinalIgnoreCase));
        public static bool IsNonCommercial(string id) => id != null && id.IndexOf("-NC", StringComparison.OrdinalIgnoreCase) >= 0;
        public static bool IsNoDerivatives(string id) => id != null && id.IndexOf("-ND", StringComparison.OrdinalIgnoreCase) >= 0;
        public static bool IsShareAlike(string id) => id != null && (id.IndexOf("-SA", StringComparison.OrdinalIgnoreCase) >= 0 || id.StartsWith("GPL", StringComparison.OrdinalIgnoreCase));
        public static bool IsUnknown(string id) => string.IsNullOrEmpty(id) || id.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

        public static string LicenseUrl(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("CC0", StringComparison.OrdinalIgnoreCase)) return "https://creativecommons.org/publicdomain/zero/1.0/";
            if (id.StartsWith("CC-", StringComparison.OrdinalIgnoreCase))
            {
                var parts = id.Split('-');
                string version = parts[parts.Length - 1];
                string kind = string.Join("-", parts.Skip(1).Take(parts.Length - 2)).ToLowerInvariant();
                return "https://creativecommons.org/licenses/" + kind + "/" + version + "/";
            }
            return null;
        }

        /// <summary>Read every .vmeta under the project's Assets/ that carries a license.</summary>
        public static LicenseAuditReport Scan(string projectRoot)
        {
            var r = new LicenseAuditReport();
            string assets = Path.Combine(projectRoot ?? "", "Assets");
            if (!Directory.Exists(assets)) return r;
            foreach (var metaPath in Directory.EnumerateFiles(assets, "*" + AssetDatabase.MetaFileExtension, SearchOption.AllDirectories))
            {
                AssetMetadata m;
                try { m = DataSerializer.LoadFromJson<AssetMetadata>(metaPath); } catch { continue; }
                if (m == null || string.IsNullOrEmpty(m.License)) continue;
                string file = metaPath.Substring(0, metaPath.Length - AssetDatabase.MetaFileExtension.Length);
                var a = new LicensedAsset
                {
                    RelPath = LibraryCompanions.Rel(projectRoot, file), Name = Path.GetFileNameWithoutExtension(file),
                    License = m.License, Author = m.Author, Source = m.Source, SourceUrl = m.SourceUrl,
                };
                r.Assets.Add(a);
            }
            // one credit / warning per source item: a model's textures carry the model's license too
            foreach (var g in r.Assets.GroupBy(a => (a.SourceUrl ?? a.RelPath) + "|" + a.License, StringComparer.OrdinalIgnoreCase))
            {
                var a = g.OrderBy(x => x.RelPath.Count(c => c == '/')).ThenBy(x => x.RelPath.Length).First();
                if (NeedsCredit(a.License)) r.Credits.Add(a);
                if (IsNonCommercial(a.License)) r.NonCommercial.Add(a);
                if (IsNoDerivatives(a.License)) r.NoDerivatives.Add(a);
                if (IsShareAlike(a.License)) r.ShareAlike.Add(a);
                if (IsUnknown(a.License)) r.Unknown.Add(a);
            }
            return r;
        }

        /// <summary>A readable list of what needs a decision before shipping (empty when nothing does).</summary>
        public static string Warnings(LicenseAuditReport r, int max = 12)
        {
            var sb = new StringBuilder();
            void Block(string title, List<LicensedAsset> list, string why)
            {
                if (list.Count == 0) return;
                sb.AppendLine(title + " (" + list.Count + ") — " + why);
                foreach (var a in list.Take(max)) sb.AppendLine("  • " + a.RelPath + " — " + a.License + (string.IsNullOrEmpty(a.Source) ? "" : " · " + a.Source));
                if (list.Count > max) sb.AppendLine("  • … " + (list.Count - max) + " more");
            }
            Block("NonCommercial", r.NonCommercial, "not allowed in a game you sell.");
            Block("NoDerivatives", r.NoDerivatives, "may not be modified (re-texturing, cutting, re-rigging).");
            Block("ShareAlike", r.ShareAlike, "your derived work must use the same license.");
            Block("Unknown license", r.Unknown, "check the source before shipping.");
            return sb.ToString().TrimEnd();
        }

        public static string CreditsMarkdown(LicenseAuditReport r, string productName, string extraCredits = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Credits — " + (string.IsNullOrEmpty(productName) ? "Game" : productName));
            sb.AppendLine();
            sb.AppendLine("Made with the Vortex Engine (MIT License) — https://github.com/shadow-kernel/Vortex-Engine");
            sb.AppendLine();
            if (r.Credits.Count > 0)
            {
                sb.AppendLine("## Third-party assets");
                sb.AppendLine();
                foreach (var a in r.Credits.OrderBy(x => x.Source).ThenBy(x => x.Name))
                {
                    string lic = LicenseUrl(a.License) != null ? "[" + a.License + "](" + LicenseUrl(a.License) + ")" : a.License;
                    string title = string.IsNullOrEmpty(a.SourceUrl) ? "\"" + a.Name + "\"" : "[\"" + a.Name + "\"](" + a.SourceUrl + ")";
                    sb.AppendLine("- " + title + " by " + (string.IsNullOrEmpty(a.Author) ? "unknown author" : a.Author) +
                                  (string.IsNullOrEmpty(a.Source) ? "" : " (" + a.Source + ")") + ", licensed under " + lic + ".");
                }
                sb.AppendLine();
            }
            var thanks = r.Assets.Where(a => !NeedsCredit(a.License) && !IsUnknown(a.License)).Select(a => a.Source).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s).ToList();
            if (thanks.Count > 0)
            {
                sb.AppendLine("## Thanks");
                sb.AppendLine();
                sb.AppendLine("Public-domain and royalty-free assets from " + string.Join(", ", thanks) + ".");
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(extraCredits))
            {
                sb.AppendLine("## Additional credits");
                sb.AppendLine();
                sb.AppendLine(extraCredits.Trim());
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>Write CREDITS.md into <paramref name="outputDir"/> when the project has third-party assets or its own
        /// ATTRIBUTIONS file. Returns the path (null when there is nothing to credit) and the number of credited items.</summary>
        public static string WriteCredits(string projectRoot, string outputDir, string productName, out int credited)
        {
            var r = Scan(projectRoot);
            credited = r.Credits.Count;
            string extra = null;
            foreach (var n in new[] { "ATTRIBUTIONS.md", "ATTRIBUTIONS.txt", "CREDITS.md" })
            {
                string f = Path.Combine(projectRoot, n);
                if (File.Exists(f)) { try { extra = File.ReadAllText(f); } catch { } break; }
            }
            if (r.Assets.Count == 0 && string.IsNullOrWhiteSpace(extra)) return null;
            Directory.CreateDirectory(outputDir);
            string path = Path.Combine(outputDir, "CREDITS.md");
            File.WriteAllText(path, CreditsMarkdown(r, productName, extra), Encoding.UTF8);
            return path;
        }
    }
}
