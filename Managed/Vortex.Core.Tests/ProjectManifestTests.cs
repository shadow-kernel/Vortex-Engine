using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using Editor.Core;
using Editor.Core.Data;
using Editor.Core.Serialization;
using Editor.Core.Services;

namespace VortexTests
{
    /// <summary>#414: opening and saving a project keeps project.vortex's "version" and "createdAt" byte for byte, and
    /// everything else the editor does not own — each scene's loadOnStart and keys the engine does not know; a new
    /// project still starts at 1.0.0 with its creation time. Load and save go through the same code as ProjectService
    /// (FromManifest / ManifestForSave) without touching a real project folder.</summary>
    public static class ProjectManifestTests
    {
        private const string Lobby = "dcad0628-9ade-4b2d-829e-8de77a0cd138";
        private const string Match = "b0c83b4a-ea81-4a95-9031-f918e6ca8655";

        /// <summary>The token DataContractJsonSerializer writes for a date on this machine ("\/Date(ms+hhmm)\/").</summary>
        private static string DateToken(DateTime local) => Token(DataSerializer.ToJson(new ProjectManifest { CreatedAt = local }), "createdAt");

        private static string Token(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|[^,\\r\\n}]+)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        /// <summary>A project.vortex as a release tool leaves it; <paramref name="created"/> null = without version, lastModified
        /// and createdAt (a hand-written file).</summary>
        private static string Manifest(string created) =>
            "{\n" +
            "  \"id\": \"a1cb32ad-19e2-4796-aa3f-1515eff8926d\",\n" +
            "  \"name\": \"My Spectre\",\n" +
            (created != null ? "  \"version\": \"2.5.1\",\n" : "") +
            "  \"engineVersion\": \"3.0.3\",\n" +
            (created != null ? "  \"lastModified\": " + created + ",\n  \"createdAt\": " + created + ",\n" : "") +
            "  \"releaseChannel\": \"beta\",\n" +
            "  \"thumbnailPath\": \".ve/icon.png\",\n" +
            "  \"startSceneId\": \"" + Lobby + "\",\n" +
            "  \"lastOpenSceneId\": \"" + Match + "\",\n" +
            "  \"formatVersion\": 2,\n" +
            "  \"scenes\": [\n" +
            "    { \"id\": \"" + Lobby + "\", \"name\": \"Lobby\", \"path\": \"Lobby.vscene\", \"loadOnStart\": true, \"streaming\": \"near\" },\n" +
            "    { \"id\": \"" + Match + "\", \"name\": \"Match\", \"path\": \"Match.vscene\", \"loadOnStart\": false }\n" +
            "  ],\n" +
            "  \"settings\": { \"companyName\": \"Paddi\", \"productName\": \"My Spectre\", \"vSync\": false, \"targetFPS\": 144, \"steamAppId\": 480 }\n" +
            "}";

        /// <summary>Open (the load's mapping, the scenes as the editor holds them) and save (the manifest written over the file).</summary>
        private static string OpenAndSave(string fileText, string dir, out ProjectData project)
        {
            var loaded = DataSerializer.FromJson<ProjectManifest>(fileText);
            project = ProjectService.FromManifest(loaded, dir);
            foreach (var r in loaded.Scenes)
                project.Scenes.Add(new Scene(project, r.Name) { Id = r.Id, FilePath = System.IO.Path.Combine(dir, "Assets", "Scenes", r.RelativePath) });
            project.ActiveScene = project.Scenes[1];
            project.LastModified = DateTime.Now;   // SaveProject stamps the save time
            return DataSerializer.ToJson(ProjectService.ManifestForSave(project, DataSerializer.FromJson<ProjectManifest>(fileText)));
        }

        [Test]
        public static void SavingKeepsVersionCreatedAtAndWhatTheEditorDoesNotOwn(TestContext t)
        {
            string created = DateToken(new DateTime(2025, 3, 14, 9, 26, 53, 589, DateTimeKind.Local));
            string file = Manifest(created);
            string saved = OpenAndSave(file, t.Dir, out var project);
            using var doc = JsonDocument.Parse(saved);   // still JSON
            var root = doc.RootElement;
            t.Equal("\"2.5.1\"", Token(saved, "version"), "version, byte for byte");
            t.Equal(created, Token(saved, "createdAt"), "createdAt, byte for byte");
            t.Equal("beta", root.GetProperty("releaseChannel").GetString(), "a key the engine does not know");
            var scenes = root.GetProperty("scenes");
            t.Equal(2, scenes.GetArrayLength(), "scene list");
            t.True(scenes[0].GetProperty("loadOnStart").GetBoolean(), "a scene's loadOnStart");
            t.Equal("near", scenes[0].GetProperty("streaming").GetString(), "a scene entry's own key");
            t.Equal(480, root.GetProperty("settings").GetProperty("steamAppId").GetInt32(), "a setting the engine does not know");
            t.Equal(144, root.GetProperty("settings").GetProperty("targetFPS").GetInt32(), "a known setting");
            // what the editor owns is stamped
            t.Equal(EngineInfo.VersionString, root.GetProperty("engineVersion").GetString(), "engineVersion");
            t.Equal(EngineInfo.CurrentProjectFormatVersion, root.GetProperty("formatVersion").GetInt32(), "formatVersion");
            t.Equal(Match, root.GetProperty("lastOpenSceneId").GetString(), "last open scene");
            t.Equal(Lobby, root.GetProperty("startSceneId").GetString(), "start scene");
            t.False(Token(saved, "lastModified") == created, "lastModified is the save time");
            // and a second session changes nothing more
            string again = OpenAndSave(saved, t.Dir, out _);
            t.Equal(Token(saved, "version"), Token(again, "version"), "version after two saves");
            t.Equal(Token(saved, "createdAt"), Token(again, "createdAt"), "createdAt after two saves");
            t.Equal("near", JsonDocument.Parse(again).RootElement.GetProperty("scenes")[0].GetProperty("streaming").GetString(), "unknown keys after two saves");
        }

        [Test]
        public static void ANewProjectStartsAt100WithItsCreationTime(TestContext t)
        {
            var project = new ProjectData(t.Dir, "Fresh") { LastModified = DateTime.Now };
            var first = ProjectService.ManifestForSave(project, null);
            t.Equal("1.0.0", first.Version, "version of a new project");
            t.True(Math.Abs((first.CreatedAt - DateTime.Now).TotalMinutes) < 1, "created now");
            t.True(project.CreatedAt == first.CreatedAt, "the project remembers its creation date");
            string firstJson = DataSerializer.ToJson(first);
            // the next save of the same session (the file on disk now exists) writes the same date
            System.Threading.Thread.Sleep(20);
            project.LastModified = DateTime.Now;
            string second = DataSerializer.ToJson(ProjectService.ManifestForSave(project, DataSerializer.FromJson<ProjectManifest>(firstJson)));
            t.Equal(Token(firstJson, "createdAt"), Token(second, "createdAt"), "createdAt of the second save");
        }

        [Test]
        public static void AManifestWithoutCreatedAtStillSaves(TestContext t)
        {
            // a hand-written or tool-written file: no createdAt read back as 0001-01-01, which the serializer refused to
            // write in a time zone east of UTC — every save of such a project failed
            var loaded = DataSerializer.FromJson<ProjectManifest>(Manifest(null));
            var project = ProjectService.FromManifest(loaded, t.Dir);
            t.True(project.CreatedAt == null, "no creation date is read as none");
            t.Equal("1.0.0", project.Version, "no version is 1.0.0");
            project.LastModified = DateTime.Now;
            string saved = DataSerializer.ToJson(ProjectService.ManifestForSave(project, loaded));
            t.True(Token(saved, "createdAt")?.StartsWith("\"\\/Date(") == true, "a creation date is written: " + Token(saved, "createdAt"));
            t.Equal("\"1.0.0\"", Token(saved, "version"), "version written");
            t.Equal("beta", JsonDocument.Parse(saved).RootElement.GetProperty("releaseChannel").GetString(), "unknown keys kept here too");
        }
    }
}
