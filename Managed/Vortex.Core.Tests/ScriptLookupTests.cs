using System.Linq;
using Editor.Core.Claude;

namespace VortexTests
{
    /// <summary>#456 / #494: the script tools (read, write, edit, attach) find a script by its bare name in any subfolder of
    /// Assets/Scripts, report two scripts of one name instead of picking one, and point at similar names.</summary>
    public static class ScriptLookupTests
    {
        private static readonly string[] Project =
        {
            "Assets/Scripts/Door.cs",
            "Assets/Scripts/Player/Weapon.cs",
            "Assets/Scripts/Gameplay/MissionManager.cs",
            "Assets/Scripts/AI/Guard.cs",
        };

        [Test]
        public static void BareNameFindsTheScriptInItsSubfolder(TestContext t)
        {
            t.Equal("Assets/Scripts/Player/Weapon.cs", ScriptLookup.Resolve("Weapon", Project, mustExist: true).Path, "bare name");
            t.Equal("Assets/Scripts/Player/Weapon.cs", ScriptLookup.Resolve("Weapon.cs", Project, mustExist: true).Path, "name with .cs");
            t.Equal("Assets/Scripts/Player/Weapon.cs", ScriptLookup.Resolve(" weapon ", Project, mustExist: true).Path, "any case, the file's own spelling back");
            t.Equal("Assets/Scripts/Gameplay/MissionManager.cs", ScriptLookup.Resolve("MissionManager", Project, mustExist: true).Path, "the report's MissionManager");
            t.Equal("Assets/Scripts/Door.cs", ScriptLookup.Resolve("Door", Project, mustExist: true).Path, "top level as before");
            t.True(ScriptLookup.Resolve("Weapon", Project, mustExist: true).Exists, "an existing script");
        }

        [Test]
        public static void PathsNameTheirFileExactly(TestContext t)
        {
            foreach (var p in new[] { "Player/Weapon", "Player/Weapon.cs", "Player\\Weapon.cs", "Scripts/Player/Weapon.cs", "Assets/Scripts/Player/Weapon.cs", "./Player/Weapon" })
                t.Equal("Assets/Scripts/Player/Weapon.cs", ScriptLookup.Resolve(p, Project, mustExist: true).Path, p);
            var wrongFolder = ScriptLookup.Resolve("AI/Weapon", Project, mustExist: true);
            t.True(wrongFolder.Path == null && wrongFolder.Error.Contains("Assets/Scripts/AI/Weapon.cs"), "a path is not searched elsewhere: " + wrongFolder.Error);
            t.True(wrongFolder.Error.Contains("Assets/Scripts/Player/Weapon.cs"), "but the error names the script of that name: " + wrongFolder.Error);
        }

        [Test]
        public static void TwoScriptsOfOneNameAreAmbiguous(TestContext t)
        {
            var twice = Project.Concat(new[] { "Assets/Scripts/Enemies/Weapon.cs" }).ToArray();
            var r = ScriptLookup.Resolve("Weapon", twice, mustExist: true);
            t.True(r.Path == null && !r.Missing, "nothing is picked");
            t.True(r.Error.Contains("Assets/Scripts/Player/Weapon.cs") && r.Error.Contains("Assets/Scripts/Enemies/Weapon.cs"), "every candidate is listed: " + r.Error);
            // the top-level file does not win silently either
            var topToo = ScriptLookup.Resolve("Door", Project.Concat(new[] { "Assets/Scripts/Old/Door.cs" }), mustExist: true);
            t.True(topToo.Path == null && topToo.Error.Contains("Assets/Scripts/Old/Door.cs") && topToo.Error.Contains("Assets/Scripts/Door.cs"), "top level + subfolder: " + topToo.Error);
            t.True(ScriptLookup.Resolve("Weapon", twice, mustExist: false).Error != null, "writing to an ambiguous name is refused as well");
            t.Equal("Assets/Scripts/Enemies/Weapon.cs", ScriptLookup.Resolve("Enemies/Weapon", twice, mustExist: true).Path, "a path picks one");
        }

        [Test]
        public static void UnknownNamesListSimilarScripts(TestContext t)
        {
            var typo = ScriptLookup.Resolve("Weapn", Project, mustExist: true);
            t.True(typo.Missing && typo.Path == null, "not found");
            t.True(typo.Error.Contains("Similar: Assets/Scripts/Player/Weapon.cs"), "a typo away: " + typo.Error);
            var part = ScriptLookup.Resolve("Mission", Project, mustExist: true);
            t.True(part.Error.Contains("Assets/Scripts/Gameplay/MissionManager.cs"), "part of a name: " + part.Error);
            var none = ScriptLookup.Resolve("Zebra", Project, mustExist: true);
            t.True(none.Error.Contains("list_scripts"), "nothing close: " + none.Error);
            t.True(ScriptLookup.Resolve("Door", new string[0], mustExist: true).Error.Contains("no scripts yet"), "an empty project");
            t.True(ScriptLookup.Resolve("  ", Project, mustExist: true).Error != null, "an empty name");
        }

        [Test]
        public static void WritingReplacesTheExistingScriptOrCreatesAtTheTop(TestContext t)
        {
            var existing = ScriptLookup.Resolve("Weapon", Project, mustExist: false);
            t.True(existing.Exists && existing.Path == "Assets/Scripts/Player/Weapon.cs", "a known name is replaced where it is (no second Weapon at the top)");
            var fresh = ScriptLookup.Resolve("FlickerLight", Project, mustExist: false);
            t.True(!fresh.Exists && fresh.Path == "Assets/Scripts/FlickerLight.cs", "a new name goes to the top of Assets/Scripts");
            var folder = ScriptLookup.Resolve("AI/Patrol.cs", Project, mustExist: false);
            t.True(!folder.Exists && folder.Path == "Assets/Scripts/AI/Patrol.cs" && folder.SameName.Count == 0, "a new script in a folder");
            var twin = ScriptLookup.Resolve("AI/Weapon.cs", Project, mustExist: false);
            t.Equal("Assets/Scripts/AI/Weapon.cs", twin.Path, "an explicit new path is honoured");
            t.True(twin.SameName.SequenceEqual(new[] { "Assets/Scripts/Player/Weapon.cs" }), "and the script of the same name is reported");
        }
    }
}
