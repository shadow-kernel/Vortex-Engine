using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Editor.Core;
using Editor.Core.Data;
using Editor.Core.Migration;
using Editor.Core.Serialization;
using Editor.Core.Services;

namespace VortexTests
{
    /// <summary>#414: opening and saving a project keeps project.vortex's "version" and "createdAt" byte for byte, every key
    /// the engine does not know (whatever its name), each scene entry's loadOnStart, and values a release script changed in
    /// the file while the editor had the project open; a new project still starts at 1.0.0 with its creation time. Open and
    /// save go through ProjectService's own code (FromManifest, RememberManifest, SaveManifestFile) on files in the test's
    /// folder.</summary>
    public static class ProjectManifestTests
    {
        private const string Lobby = "dcad0628-9ade-4b2d-829e-8de77a0cd138";
        private const string Match = "b0c83b4a-ea81-4a95-9031-f918e6ca8655";

        /// <summary>A project.vortex as a release tool and a schema-aware editor leave it: key names that are no XML names,
        /// values of every JSON type, a createdAt written in another time zone (+0500).</summary>
        private static readonly string Wild = string.Join("\n", new[]
        {
            "{",
            "  \"$schema\": \"https://example.com/vortex-project.schema.json\",",
            "  \"id\": \"a1cb32ad-19e2-4796-aa3f-1515eff8926d\",",
            "  \"name\": \"My Spectre\",",
            "  \"version\": \"2.5.1\",",
            "  \"engineVersion\": \"3.0.3\",",
            "  \"lastModified\": \"\\/Date(1791542954335+0200)\\/\",",
            "  \"createdAt\": \"\\/Date(1700000000000+0500)\\/\",",
            "  \"release notes\": \"Grüße \\\"quoted\\\" ☃ / slash\",",
            "  \"steam:depot\": 1,",
            "  \"1st\": true,",
            "  \"über\": \"umlaut\",",
            "  \"x-release\": { \"build-number\": 42, \"$ref\": \"#/defs/a\", \"nested key\": [ { \"k k\": 1 } ] },",
            "  \"numbers\": [ 12345678901234567890, 1.5e-7, 1e400, -0.0, 0.1 ],",
            "  \"empty\": {},",
            "  \"nothing\": null,",
            "  \"thumbnailPath\": \".ve/icon.png\",",
            "  \"startSceneId\": \"" + Lobby + "\",",
            "  \"lastOpenSceneId\": \"" + Match + "\",",
            "  \"formatVersion\": 2,",
            "  \"scenes\": [",
            "    { \"id\": \"" + Lobby + "\", \"name\": \"Lobby\", \"path\": \"Lobby.vscene\", \"loadOnStart\": true, \"load order\": 1, \"tags\": [\"menu\"] },",
            "    { \"id\": \"" + Match + "\", \"name\": \"Match\", \"path\": \"Match.vscene\", \"loadOnStart\": false }",
            "  ],",
            "  \"settings\": { \"companyName\": \"Paddi\", \"productName\": \"My Spectre\", \"vSync\": false, \"targetFPS\": 144, \"steam:appId\": 480, \"deep\": { \"a\": [1] } }",
            "}",
        });

        private static string ManifestPath(string dir) => Path.Combine(dir, ProjectService.ManifestFileName);

        /// <summary>What LoadProjectFromManifest does with the manifest (without loading .vscene files).</summary>
        private static ProjectData Open(string dir)
        {
            var m = DataSerializer.FromJson<ProjectManifest>(File.ReadAllText(ManifestPath(dir), Encoding.UTF8));
            var p = ProjectService.FromManifest(m, dir);
            foreach (var r in m.Scenes ?? new List<SceneReference>())
                p.Scenes.Add(new Scene(p, r.Name) { Id = r.Id, FilePath = Path.Combine(dir, "Assets", "Scenes", r.RelativePath) });
            p.ActiveScene = p.Scenes.FirstOrDefault(s => s.Id == m.LastOpenSceneId) ?? p.Scenes.FirstOrDefault();
            ProjectService.RememberManifest(p);
            return p;
        }

        /// <summary>What SaveProject does with the manifest.</summary>
        private static string Save(ProjectData p)
        {
            p.LastModified = DateTime.Now;
            ProjectService.SaveManifestFile(p);
            return File.ReadAllText(ManifestPath(p.Path), Encoding.UTF8);
        }

        private static string Raw(JsonElement root, string key) => root.TryGetProperty(key, out var v) ? v.GetRawText() : null;

        [Test]
        public static void SavingKeepsVersionCreatedAtAndEveryKeyTheEditorDoesNotOwn(TestContext t)
        {
            string dir = t.Path("wild");
            t.Write("wild/project.vortex", Wild);
            string saved = Save(Open(dir));
            using var before = JsonDocument.Parse(Wild);
            using var after = JsonDocument.Parse(saved);
            var b = before.RootElement;
            var a = after.RootElement;
            // the editor's own: the stamps
            t.Equal(EngineInfo.VersionString, a.GetProperty("engineVersion").GetString(), "engineVersion stamped");
            t.Equal(EngineInfo.CurrentProjectFormatVersion, a.GetProperty("formatVersion").GetInt32(), "formatVersion stamped");
            t.False(Raw(a, "lastModified") == Raw(b, "lastModified"), "lastModified is the save time");
            // everything else exactly as the file had it, in the file's order — the key names were what made every save throw
            foreach (var p in b.EnumerateObject().Where(p => p.Name != "lastModified" && p.Name != "engineVersion" && p.Name != "scenes"))
                t.Equal(p.Value.GetRawText(), Raw(a, p.Name), "'" + p.Name + "' byte for byte");
            t.True(b.EnumerateObject().Select(p => p.Name).SequenceEqual(a.EnumerateObject().Select(p => p.Name)), "the file's key order");
            t.True(saved.Contains("\"createdAt\": \"\\/Date(1700000000000+0500)\\/\""), "createdAt keeps its own offset in any time zone");
            var lobby = a.GetProperty("scenes")[0];
            t.True(lobby.GetProperty("loadOnStart").GetBoolean(), "a scene's loadOnStart");
            t.Equal(1, lobby.GetProperty("load order").GetInt32(), "a scene entry's own key");
            t.Equal("[\"menu\"]", Raw(lobby, "tags"), "a scene entry's list");
            t.False(a.GetProperty("scenes")[1].TryGetProperty("tags", out _), "no key added to an entry that had none");
            // and the next session changes nothing but the save time
            string again = Save(Open(dir));
            t.Equal(saved.Replace(Raw(a, "lastModified"), "T"), again.Replace(Raw(JsonDocument.Parse(again).RootElement, "lastModified"), "T"), "a second save");
        }

        [Test]
        public static void AnUnchangedProjectIsWrittenByteForByte(TestContext t)
        {
            // a file the editor wrote (DataContractJsonSerializer: CRLF, "[ ]", "\/" escapes, a byte order mark) …
            string dir = t.Path("same");
            Directory.CreateDirectory(dir);
            var fresh = new ProjectData(dir, "Same");
            fresh.ActiveScene = fresh.Scenes[0];
            fresh.Settings.IconPath = "Assets/UI/icon.png";
            ProjectService.SaveManifestFile(fresh);
            byte[] first = File.ReadAllBytes(ManifestPath(dir));
            // … opened and saved without a change (the save time aside) is the same file
            ProjectService.SaveManifestFile(Open(dir));
            byte[] second = File.ReadAllBytes(ManifestPath(dir));
            t.True(first.SequenceEqual(second), "byte for byte:\n" + Encoding.UTF8.GetString(first) + "\n---\n" + Encoding.UTF8.GetString(second));
            t.True(first[0] == 0xEF && first[1] == 0xBB && first[2] == 0xBF, "a new file has the byte order mark, as before");
        }

        [Test]
        public static void ChangesInTheFileWhileTheProjectIsOpenSurvive(TestContext t)
        {
            string dir = t.Path("bump");
            t.Write("bump/project.vortex", Wild.Replace("\"2.5.1\"", "\"1.3.3\""));
            var p = Open(dir);
            // the release script bumps the version, adds a key and changes two settings while the editor is open …
            File.WriteAllText(ManifestPath(dir), Wild.Replace("\"2.5.1\"", "\"1.3.4\"").Replace("\"1st\": true,", "\"1st\": true,\n  \"releasedAt\": \"2026-10-14\",")
                                                    .Replace("\"vSync\": false", "\"vSync\": true").Replace("\"companyName\": \"Paddi\"", "\"companyName\": \"Disk Co\""));
            // … and the user changes two settings in Project Settings
            p.Settings.TargetFPS = 60;
            p.Settings.CompanyName = "Editor Co";
            using var after = JsonDocument.Parse(Save(p));
            var a = after.RootElement;
            t.Equal("1.3.4", a.GetProperty("version").GetString(), "the bumped version is not reverted");
            t.Equal("2026-10-14", a.GetProperty("releasedAt").GetString(), "the key added meanwhile");
            var s = a.GetProperty("settings");
            t.True(s.GetProperty("vSync").GetBoolean(), "a setting changed only in the file");
            t.Equal(60, s.GetProperty("targetFPS").GetInt32(), "a setting changed in the editor");
            t.Equal("Editor Co", s.GetProperty("companyName").GetString(), "changed in both: the editor's save wins");
            t.Equal(480, s.GetProperty("steam:appId").GetInt32(), "a setting the engine does not know");
        }

        [Test]
        public static void TheSceneListIsTheEditorsAndEntriesKeepTheirKeys(TestContext t)
        {
            string dir = t.Path("scenes");
            t.Write("scenes/project.vortex", Wild);
            var p = Open(dir);
            p.Scenes[0].Name = "Lobby Renamed";
            p.Scenes.RemoveAt(1);
            p.Scenes.Add(new Scene(p, "Brand New"));
            using var after = JsonDocument.Parse(Save(p));
            var scenes = after.RootElement.GetProperty("scenes");
            t.Equal(2, scenes.GetArrayLength(), "renamed + new, the removed one gone");
            t.Equal("Lobby Renamed", scenes[0].GetProperty("name").GetString(), "the editor's name");
            t.True(scenes[0].GetProperty("loadOnStart").GetBoolean() && scenes[0].GetProperty("load order").GetInt32() == 1, "the renamed entry keeps its keys");
            t.Equal("Brand New", scenes[1].GetProperty("name").GetString(), "a new scene");
            t.Equal("Brand New.vscene", scenes[1].GetProperty("path").GetString(), "its file");
            t.False(scenes[1].GetProperty("loadOnStart").GetBoolean(), "with the defaults");
        }

        [Test]
        public static void ANewProjectStartsAt100WithItsCreationTime(TestContext t)
        {
            string dir = t.Path("fresh");
            Directory.CreateDirectory(dir);
            var project = new ProjectData(dir, "Fresh");
            string first = Save(project);
            using var doc = JsonDocument.Parse(first);
            t.Equal("1.0.0", doc.RootElement.GetProperty("version").GetString(), "version of a new project");
            var created = DataSerializer.FromJson<ProjectManifest>(first).CreatedAt;
            t.True(Math.Abs((created - DateTime.Now).TotalMinutes) < 1, "created now");
            System.Threading.Thread.Sleep(20);
            string second = Save(project);   // the next save of the same session
            t.Equal(Raw(doc.RootElement, "createdAt"), Raw(JsonDocument.Parse(second).RootElement, "createdAt"), "createdAt of the second save");
        }

        [Test]
        public static void AHandWrittenFileStaysAsSmallAsItIs(TestContext t)
        {
            // no version, no dates, no settings: 0001-01-01 used to make every save throw east of UTC
            string dir = t.Path("small");
            string text = "{\n  \"id\": \"a1cb32ad-19e2-4796-aa3f-1515eff8926d\",\n  \"name\": \"Old\",\n  \"scenes\": [ { \"id\": \"" + Lobby + "\", \"name\": \"Lobby\", \"path\": \"Lobby.vscene\" } ]\n}\n";
            t.Write("small/project.vortex", text);
            var p = Open(dir);
            t.True(p.CreatedAt != null && p.Version == "1.0.0", "read: version 1.0.0, a creation date to hold in memory");
            string saved = Save(p);
            using var doc = JsonDocument.Parse(saved);
            var a = doc.RootElement;
            t.False(a.TryGetProperty("createdAt", out _) || a.TryGetProperty("version", out _) || a.TryGetProperty("settings", out _), "nothing the file did not have and the editor did not change: " + saved);
            t.True(a.TryGetProperty("lastModified", out _) && a.TryGetProperty("engineVersion", out _) && a.TryGetProperty("formatVersion", out _), "the stamps");
            t.False(a.GetProperty("scenes")[0].TryGetProperty("loadOnStart", out _), "the entry as it was");
            t.True(saved.EndsWith("}\n", StringComparison.Ordinal) && !saved.Contains('\r'), "its line endings");
            t.False(File.ReadAllBytes(ManifestPath(dir))[0] == 0xEF, "no byte order mark added");
        }

        [Test]
        public static void AnUnreadableFileIsReplacedNotKept(TestContext t)
        {
            string dir = t.Path("broken");
            t.Write("broken/project.vortex", Wild);
            var p = Open(dir);
            File.WriteAllText(ManifestPath(dir), "{ \"id\": \"a1cb32ad-19e2-4796-aa3f-1515eff8926d\", \"name\": ");   // cut off on disk
            string saved = Save(p);
            var back = DataSerializer.FromJson<ProjectManifest>(saved);
            t.Equal("My Spectre", back.Name, "the editor's manifest is written");
            t.Equal(2, back.Scenes.Count, "with its scenes");
        }

        [Test]
        public static void AnUpgradeStampKeepsTheKeysToo(TestContext t)
        {
            // a v1 project with a "$schema" key: the stamp threw, so the upgrade was offered (with a full backup) on every open
            string dir = t.Path("v1");
            string text = Wild.Replace("\"formatVersion\": 2", "\"formatVersion\": 1").Replace("\"3.0.3\"", "\"2.3.0\"");
            t.Write("v1/project.vortex", text);
            var m = DataSerializer.FromJson<ProjectManifest>(text);
            t.True(ProjectMigrationService.Migrate(dir, m, ManifestPath(dir)), "migrated");
            using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath(dir), Encoding.UTF8));
            var a = doc.RootElement;
            t.Equal(ProjectMigrationService.Current, a.GetProperty("formatVersion").GetInt32(), "stamped");
            t.Equal("https://example.com/vortex-project.schema.json", a.GetProperty("$schema").GetString(), "'$schema' kept");
            t.Equal("\"Grüße \\\"quoted\\\" ☃ / slash\"", Raw(a, "release notes"), "'release notes' kept");
            t.Equal(MigrationStatus.UpToDate, ProjectMigrationService.Evaluate(DataSerializer.FromJson<ProjectManifest>(File.ReadAllText(ManifestPath(dir)))).Status, "not offered again");
        }
    }
}
