using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Editor.Core.Claude
{
    /// <summary>
    /// The project's <c>.mcp.json</c> (Claude Code's project-scoped MCP servers): adds or updates the Vortex entry
    /// <c>{"mcpServers": {"vortex": {"type": "http", "url": "http://127.0.0.1:7420/mcp"}}}</c> and keeps every other
    /// server and key the file already has — users may run other servers in the same project.
    /// </summary>
    public static class McpProjectConfig
    {
        public const string FileName = ".mcp.json";

        public enum Outcome { Created, Updated, Unchanged }

        public static string PathFor(string projectDir) => Path.Combine(projectDir, FileName);

        /// <summary>Write the entry into &lt;project&gt;/.mcp.json (created when missing).</summary>
        public static Outcome Write(string projectDir, string serverName, string url)
        {
            string path = PathFor(projectDir);
            string existing = File.Exists(path) ? File.ReadAllText(path) : null;
            string merged = Merge(existing, serverName, url, out var outcome);
            if (outcome != Outcome.Unchanged) File.WriteAllText(path, merged);
            return outcome;
        }

        /// <summary>Whether the file already points <paramref name="serverName"/> at <paramref name="url"/>.</summary>
        public static bool IsConfigured(string projectDir, string serverName, string url)
        {
            try
            {
                string path = PathFor(projectDir);
                if (!File.Exists(path)) return false;
                Merge(File.ReadAllText(path), serverName, url, out var outcome);
                return outcome == Outcome.Unchanged;
            }
            catch { return false; }
        }

        /// <summary>The merged file content. Throws <see cref="InvalidDataException"/> when the existing file is not a JSON
        /// object (it is never overwritten blindly).</summary>
        public static string Merge(string existingJson, string serverName, string url, out Outcome outcome)
        {
            JsonObject root;
            bool created = string.IsNullOrWhiteSpace(existingJson);
            if (created) root = new JsonObject();
            else
            {
                JsonNode parsed;
                try { parsed = JsonNode.Parse(existingJson, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
                catch (JsonException ex) { throw new InvalidDataException(FileName + " is not valid JSON (" + ex.Message + ") — fix or remove it first."); }
                root = parsed as JsonObject ?? throw new InvalidDataException(FileName + " does not contain a JSON object.");
            }

            if (!(root["mcpServers"] is JsonObject servers))
            {
                if (root["mcpServers"] != null) throw new InvalidDataException("\"mcpServers\" in " + FileName + " is not an object.");
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            if (servers[serverName] is JsonObject current
                && (string)current["type"] == "http" && (string)current["url"] == url)
            {
                outcome = Outcome.Unchanged;
                return existingJson;
            }

            // keep extra keys of an existing entry (headers, …); type and url are ours
            if (!(servers[serverName] is JsonObject entry))
            {
                entry = new JsonObject();
                servers[serverName] = entry;
            }
            entry["type"] = "http";
            entry["url"] = url;
            outcome = created ? Outcome.Created : Outcome.Updated;
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
        }
    }
}
