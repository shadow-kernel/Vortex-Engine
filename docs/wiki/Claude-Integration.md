# Claude Integration

Claude can operate the Vortex editor: build and change scenes, place and configure entities, lights and
components, look at the viewport, run the game and read the console. The editor runs a **Model Context Protocol
(MCP) server**; Claude Code and Claude Desktop connect to it and use its [tools](Claude-Tools). Your own Claude
subscription does the thinking — the editor needs no API key for this.

<img src="https://raw.githubusercontent.com/shadow-kernel/Vortex-Engine/main/docs/showcase/vortex-3.0-claude.webp" width="100%" alt="Claude Code builds and sounds a horror corridor in the Vortex editor through the MCP server: one prompt, 85 tool calls in 2.7 minutes, sped up"/>

*One prompt in Claude Code: "build a short horror corridor and fully sound it". The editor's MCP server did the rest:
85 tool calls in 2.7 minutes, recorded and sped up. In short:*

1. In the editor: **Tools ▸ Claude ▸ Connect Claude Code / Desktop…**, then **Start**.
2. Copy the one command it shows (`claude mcp add --transport http vortex http://127.0.0.1:<port>/mcp`), or press
   **Write .mcp.json to Project**.
3. Ask Claude Code for what you want. It builds, compiles scripts, looks at the viewport and play-tests.
4. Every call is **one undo step**. *Tools ▸ Claude ▸ Operations…* lists them with their file diffs and reverts any of
   them.

The sections below cover each step in detail.

> Shipped in [v3.0.0](https://github.com/shadow-kernel/Vortex-Engine/releases/tag/v3.0.0) and checked end to end with
> real Claude Code sessions ([Release Process](Release-Process#claude-end-to-end)). Architecture and decisions:
> [[Design-Claude-Integration]].

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

## 4. The Claude panel (inside the editor)

The **Claude** tab next to Inspector and Environment (**Window ▸ Claude**, ⌘/Ctrl+9) is a chat with the same tools —
no terminal needed. It uses **your own Anthropic API key** (the gear button in the panel, or Asset Store ▸ API Keys…;
billed per token by Anthropic). The key is stored only on this computer (owner-only file on macOS/Linux, encrypted for
your Windows account with DPAPI) and is sent nowhere but `api.anthropic.com`. The panel works with the MCP server off.

- Pick the model in the header (Opus 5.5 most capable, Sonnet 5.5 faster and cheaper, Haiku 4.5 fastest). The line
  under the input shows the tokens used; the tool definitions are prompt-cached, so long conversations stay cheap.
- Answers stream in; every tool call is a card — click it for the input, the result and, for `capture_viewport`, the image.
- **Approval:** tools that only read run immediately. A tool that changes something asks first: **Allow**, **Always
  allow** (for this project — kept in your app data, not in the project) or **Deny** (Claude is told and asks instead).
  *… ▸ Reset “Always allow”* asks again.
- **Stop** (or Esc) ends the turn; finished tool calls stay done and stay undoable.

## 5. Operations, dry runs, reverting

**Tools ▸ Claude ▸ Operations…** lists every tool call of the session — from the panel and from Claude Code — with
time, client, input and result. **Revert** takes one back: the newest like Undo, an older one out of order (a warning
lists later operations that touched the same entities or files first). **Diff** shows what a call changed in a file
(scripts, materials).

Every tool that changes the project accepts **`dry_run: true`**: the call runs, reports what it *would* create, remove,
change and write — and is rolled back completely (scene, undo history and files). Claude uses it to show a plan before a
big change; you can ask for it too ("show me the plan first").

## 6. How Claude works in the editor

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

## 7. Troubleshooting

| Problem | Fix |
|---|---|
| Status bar red: *port 7420 is already in use* | Another Vortex editor (or another program) has the port. Close it, or pick another port in the dialog and register that URL in Claude. |
| `claude mcp list` shows *failed* | The editor is not running, the server is off, or the port differs from the registered URL. Open the dialog and compare. |
| Claude says no project is open | The tools work on the project open in the editor — open one first. |
| `capture_viewport` times out | The editor window is minimised or a preview window pauses the main viewport — restore the window. |
| A firewall asks about Vortex | Not needed: the server listens on loopback only; you can deny network access. |
| Panel: "the Anthropic API key was rejected" | Re-enter the key (gear button); keys start with `sk-ant-`. |
| Panel: "rate limited" / "overloaded" | Wait a moment and send again; long tool loops can hit your organisation's rate limit. |

Every tool with its parameters: [[Claude-Tools]] (generated from the code).
