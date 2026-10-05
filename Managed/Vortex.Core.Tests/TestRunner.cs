using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VortexTests
{
    /// <summary>Marks a test method: <c>public static void Name(TestContext t)</c>.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public sealed class TestFailure : Exception { public TestFailure(string m) : base(m) { } }

    /// <summary>Per-test scratch folder + assertions.</summary>
    public sealed class TestContext
    {
        public string Dir { get; }
        public TestContext(string dir) { Dir = dir; Directory.CreateDirectory(dir); }

        public string Path(params string[] parts) => System.IO.Path.Combine(new[] { Dir }.Concat(parts).ToArray());

        public string Write(string rel, string text)
        {
            var p = Path(rel.Split('/'));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
            return p;
        }

        public string Write(string rel, byte[] bytes)
        {
            var p = Path(rel.Split('/'));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
            File.WriteAllBytes(p, bytes);
            return p;
        }

        public void True(bool cond, string what) { if (!cond) throw new TestFailure("expected true: " + what); }
        public void False(bool cond, string what) { if (cond) throw new TestFailure("expected false: " + what); }
        public void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new TestFailure(what + ": expected <" + expected + "> but was <" + actual + ">");
        }
        public void NotNull(object o, string what) { if (o == null) throw new TestFailure("expected a value: " + what); }
    }

    public static class TestRunner
    {
        public static int Main(string[] args)
        {
            string filter = args.FirstOrDefault();
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vortex-core-tests-" + Process.GetCurrentProcess().Id);
            // never touch the user's editor state or library
            Environment.SetEnvironmentVariable("VORTEX_APPDATA_DIR", System.IO.Path.Combine(root, "appdata"));
            Environment.SetEnvironmentVariable("VORTEX_ASSETDB_DIR", System.IO.Path.Combine(root, "assetdb-unused"));

            var tests = typeof(TestRunner).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => filter == null || (m.DeclaringType.Name + "." + m.Name).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.MetadataToken)
                .ToList();
            int failed = 0;
            var sw = Stopwatch.StartNew();
            foreach (var m in tests)
            {
                string name = m.DeclaringType.Name + "." + m.Name;
                var ctx = new TestContext(System.IO.Path.Combine(root, name));
                var t0 = sw.ElapsedMilliseconds;
                try
                {
                    var ret = m.Invoke(null, new object[] { ctx });
                    if (ret is System.Threading.Tasks.Task task)
                    {
                        try { task.GetAwaiter().GetResult(); }
                        catch (Exception ex) { throw new TargetInvocationException(ex); }
                    }
                    Console.WriteLine("  ok    " + name + "  (" + (sw.ElapsedMilliseconds - t0) + " ms)");
                }
                catch (TargetInvocationException ex)
                {
                    failed++;
                    var inner = ex.InnerException ?? ex;
                    Console.WriteLine("  FAIL  " + name + ": " + inner.Message);
                    if (!(inner is TestFailure)) Console.WriteLine(inner.StackTrace);
                }
                finally { Editor.Core.Assets.Library.GlobalAssetDatabase.ResetInstance(); }
            }
            Console.WriteLine((failed == 0 ? "PASS" : "FAILED") + ": " + (tests.Count - failed) + "/" + tests.Count + " tests in " + sw.ElapsedMilliseconds + " ms");
            try { Directory.Delete(root, true); } catch { }
            return failed == 0 ? 0 : 1;
        }
    }
}
