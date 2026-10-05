# Claude Integration

Claude can operate the Vortex editor: build and change scenes, place and configure entities, lights and
components, look at the viewport, run the game and read the console. The editor runs a **Model Context Protocol
(MCP) server**; Claude Code and Claude Desktop connect to it and use its [tools](Claude-Tools). Your own Claude
subscription does the thinking — the editor needs no API key for this.

> Milestone [v3.0.0 – Claude-Native Engine](https://github.com/shadow-kernel/Vortex-Engine/milestone/5).
> Architecture and decisions: [[Design-Claude-Integration]].

## 1. Turn on the MCP server

**Tools ▸ Claude ▸ Connect Claude Code / Desktop…** (or click **Claude** in the status bar) and press **Start**.

- The status bar shows the server: grey **Claude** = off, green **Claude :7420** = running, red = not running (hover
  for the reason). After each tool call it shows briefly what Claude did.
- The server listens on `http://127.0.0.1:7420/mcp`. Only programs on this computer can connect; web pages and other
  machines are refused. Change the port in the same dialog if 7420 is taken.
- Once turned on, it starts with the editor every time (**Tools ▸ Claude ▸ MCP Server** turns it off again).

## 2. Connect Claude Code

Run once in a terminal (the dialog shows the command with your port and a **Copy** button):

```bash
claude mcp add --transport http vortex http://127.0.0.1:7420/mcp
```

That registers the server for you (scope *local*). Alternatively press **Write .mcp.json to Project** in the dialog:
it adds the server to `<project>/.mcp.json` (other servers in the file are kept), so Claude Code started in the project
folder offers it — commit the file to share it with your team.

Then start `claude` and ask for something:

> *"Look at the open scene in Vortex and build a 12 m corridor east of the gate: walls, a ceiling, four dim ceiling
> lights. Then show me a screenshot."*

Claude sees the tools as `mcp__vortex__<tool>` (for example `mcp__vortex__create_entity`). Claude Code asks before
it uses a tool; allow the Vortex tools for the session, or for good with `/permissions` → allow `mcp__vortex__*`.
`claude mcp list` shows whether the server is reachable.

## 3. Connect Claude Desktop

Claude Desktop's *custom connectors* run through Anthropic's cloud and cannot reach `127.0.0.1`. Use a local bridge
instead: Claude Desktop ▸ **Settings ▸ Developer ▸ Edit Config** opens `claude_desktop_config.json`
(macOS `~/Library/Application Support/Claude/`, Windows `%APPDATA%\Claude\`). Add the server under `mcpServers`:

```json
{
  "mcpServers": {
    "vortex": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:7420/mcp"]
    }
  }
}
```

`mcp-remote` (an npm package; needs [Node.js](https://nodejs.org)) speaks stdio to Claude Desktop and HTTP to the
editor. **Quit Claude Desktop completely and start it again** — it reads the file only at start-up.

## 4. How Claude works in the editor

| | |
|---|---|
| **One undo step per call** | Every tool call that changes something is one entry in the undo history, named `Claude: …` (*Claude: create Lamp 3*, *Claude: delete 4 entities*). **Ctrl/⌘+Z** takes back exactly one call, however many entities it touched; **Tools ▸ History…** lists them. Claude can undo its own steps with the `undo` tool. |
| **All or nothing** | A call that fails half-way (an unknown component type on the third entity …) is rolled back completely — nothing half-built stays behind. |
| **Read-only tools** | `scene_outline`, `find_entities`, `get_entity`, `capture_viewport`, `read_console`, … never change anything (the MCP *readOnlyHint*). |
| **Errors that teach** | A wrong value comes back as an error that lists the valid ones (`light_type must be one of: Directional, Point, Spot`), ambiguous names list the matching entities with their ids — Claude corrects itself instead of guessing. |
| **It sees its work** | `capture_viewport` returns an image of the viewport (without gizmos and selection outlines); `focus_camera` frames what to look at. |
| **Nothing is saved behind your back** | Changes stay in the open scene until you save — or Claude calls `save_scene`. |
| **One at a time** | Tool calls run one after another on the editor's UI thread, so they never interleave with each other. |

Entities are addressed by their short **id** (8 hex digits, in every result), by **path** (`Level/Corridor/Lamp 2`)
or by a **unique name**. Positions are metres with Y up; rotations are Euler angles in degrees; a primitive cube is
1 m.

## 5. Troubleshooting

| Problem | Fix |
|---|---|
| Status bar red: *port 7420 is already in use* | Another Vortex editor (or another program) has the port. Close it, or pick another port in the dialog and register that URL in Claude. |
| `claude mcp list` shows *failed* | The editor is not running, the server is off, or the port differs from the registered URL. Open the dialog and compare. |
| Claude says no project is open | The tools work on the project open in the editor — open one first. |
| `capture_viewport` times out | The editor window is minimised or a preview window pauses the main viewport — restore the window. |
| A firewall asks about Vortex | Not needed: the server listens on loopback only; you can deny network access. |

Every tool with its parameters: [[Claude-Tools]] (generated from the code).
