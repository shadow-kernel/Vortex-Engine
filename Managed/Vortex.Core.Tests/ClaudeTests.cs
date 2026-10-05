using System;
using System.IO;
using System.Text.Json;
using Editor.Core.Claude;

namespace VortexTests
{
    /// <summary>Claude integration (milestone v3.0.0): the project's .mcp.json.</summary>
    public static class ClaudeTests
    {
        private const string Url = "http://127.0.0.1:7420/mcp";

        [Test]
        public static void McpJsonIsCreated(TestContext t)
        {
            var dir = t.Path("p1");
            Directory.CreateDirectory(dir);
            t.Equal(McpProjectConfig.Outcome.Created, McpProjectConfig.Write(dir, "vortex", Url), "new file");
            using var doc = JsonDocument.Parse(File.ReadAllText(McpProjectConfig.PathFor(dir)));
            var v = doc.RootElement.GetProperty("mcpServers").GetProperty("vortex");
            t.Equal("http", v.GetProperty("type").GetString(), "type");
            t.Equal(Url, v.GetProperty("url").GetString(), "url");
            t.True(McpProjectConfig.IsConfigured(dir, "vortex", Url), "configured");
            t.Equal(McpProjectConfig.Outcome.Unchanged, McpProjectConfig.Write(dir, "vortex", Url), "second write changes nothing");
        }

        [Test]
        public static void McpJsonKeepsOtherServers(TestContext t)
        {
            var dir = t.Path("p2");
            t.Write("p2/.mcp.json", "{\n  // comment\n  \"mcpServers\": {\n    \"github\": { \"type\": \"http\", \"url\": \"https://example.com/mcp\" },\n" +
                                    "    \"vortex\": { \"type\": \"http\", \"url\": \"http://127.0.0.1:9999/mcp\", \"headers\": { \"X\": \"1\" } }\n  },\n  \"other\": true,\n}");
            t.Equal(McpProjectConfig.Outcome.Updated, McpProjectConfig.Write(dir, "vortex", Url), "port changed");
            using var doc = JsonDocument.Parse(File.ReadAllText(McpProjectConfig.PathFor(dir)));
            var servers = doc.RootElement.GetProperty("mcpServers");
            t.Equal("https://example.com/mcp", servers.GetProperty("github").GetProperty("url").GetString(), "other server kept");
            t.Equal(Url, servers.GetProperty("vortex").GetProperty("url").GetString(), "url updated");
            t.Equal("1", servers.GetProperty("vortex").GetProperty("headers").GetProperty("X").GetString(), "extra keys of the entry kept");
            t.True(doc.RootElement.GetProperty("other").GetBoolean(), "other top-level keys kept");
        }

        [Test]
        public static void McpJsonNeverOverwritesBrokenFiles(TestContext t)
        {
            var dir = t.Path("p3");
            var path = t.Write("p3/.mcp.json", "{ this is not json");
            bool threw = false;
            try { McpProjectConfig.Write(dir, "vortex", Url); } catch (InvalidDataException) { threw = true; }
            t.True(threw, "broken file is reported");
            t.Equal("{ this is not json", File.ReadAllText(path), "broken file untouched");
            t.Write("p3/.mcp.json", "{ \"mcpServers\": [] }");
            threw = false;
            try { McpProjectConfig.Write(dir, "vortex", Url); } catch (InvalidDataException) { threw = true; }
            t.True(threw, "mcpServers that is not an object is reported");
        }
    }
}
