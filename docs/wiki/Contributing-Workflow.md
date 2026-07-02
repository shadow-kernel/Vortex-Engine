# Contributing Workflow

How issues, labels, milestones, and PRs work in Vortex Engine (MIT, free including commercial use).

## Label taxonomy

Click a label to see its open issues.

### Priority

| Label | Meaning |
|---|---|
| [`P0-critical`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3AP0-critical) | Blocks the milestone goal; do first |
| [`P1-high`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3AP1-high) | Core of the milestone |
| [`P2-medium`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3AP2-medium) | Should ship in the milestone, can slip |
| [`P3-low`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3AP3-low) | Nice-to-have / backlog candidate |

### Type

| Label | Meaning |
|---|---|
| [`type:epic`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Aepic) | Umbrella issue tracking a whole feature area via a task list |
| [`type:feature`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Afeature) | New functionality |
| [`type:perf`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Aperf) | Performance work (see [[Performance-Master-Plan]]) |
| [`type:refactor`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Arefactor) | Restructuring without behavior change |
| [`type:research`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Aresearch) | Spike / investigation; deliverable is a decision or design page |
| [`type:docs`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Adocs) | Documentation / wiki work |

### Area

One `area:*` label per subsystem; issues may carry more than one:
[`area:rendering`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Arendering) ·
[`area:audio`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aaudio) ·
[`area:scripting`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Ascripting) ·
[`area:physics`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aphysics) ·
[`area:animation`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aanimation) ·
[`area:ai`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aai) ·
[`area:editor`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aeditor) ·
[`area:assets`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aassets) ·
[`area:asset-store`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aasset-store) ·
[`area:claude`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aclaude) ·
[`area:ui-vui`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Aui-vui) ·
[`area:core`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Acore) ·
[`area:build-ci`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Abuild-ci) ·
[`area:networking`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Anetworking)

### Size

Rough effort estimate: [`size:S`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Asize%3AS) (hours) · [`size:M`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Asize%3AM) (a day or two) · [`size:L`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Asize%3AL) (up to a week) · [`size:XL`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Asize%3AXL) (multi-week / epic-scale)

### Special

| Label | Meaning |
|---|---|
| [`horror-blocker`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Ahorror-blocker) | Must ship before development of the first-person horror game can start (gate: v2.7.0 epic) |
| [`needs-design`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aneeds-design) | Requires a design doc / wiki page before implementation starts |
| `status:*` | Workflow state on the issue (e.g. [`status:in-progress`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Astatus%3Ain-progress), [`status:blocked`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Astatus%3Ablocked)) — set when work starts, removed on close |

## Milestones

Milestones are the release train; each has one or more `type:epic` umbrella issues.

| # | Milestone |
|---|---|
| 1 | [v2.6.0 Audio](https://github.com/shadow-kernel/Vortex-Engine/milestone/1) |
| 2 | [v2.7.0 Horror Essentials](https://github.com/shadow-kernel/Vortex-Engine/milestone/2) — the `horror-blocker` gate |
| 3 | [v2.8.0 Global Asset DB](https://github.com/shadow-kernel/Vortex-Engine/milestone/3) |
| 4 | [v2.9.0 Asset Store & Claude Sound Studio](https://github.com/shadow-kernel/Vortex-Engine/milestone/4) |
| 5 | [v3.0.0 Claude-Native Engine](https://github.com/shadow-kernel/Vortex-Engine/milestone/5) — see [[Design-Claude-Integration]] |
| 6 | [v3.1.0 Physics v2](https://github.com/shadow-kernel/Vortex-Engine/milestone/6) |
| 7 | [v3.2.0 AI & Navigation](https://github.com/shadow-kernel/Vortex-Engine/milestone/7) |
| 8 | [v3.3.0 VFX](https://github.com/shadow-kernel/Vortex-Engine/milestone/8) |
| 9 | [v3.4.0 World & Streaming](https://github.com/shadow-kernel/Vortex-Engine/milestone/9) |
| 10 | [v4.0.0 XXL 10x Performance](https://github.com/shadow-kernel/Vortex-Engine/milestone/10) — see [[Performance-Master-Plan]] |

Issues without a milestone are backlog; they get pulled into a milestone during triage.

## Issue lifecycle

1. **Triage** — new issue gets `type:*`, `area:*`, priority, `size:*` (plus `horror-blocker`/`needs-design` where applicable).
2. **Milestone** — assigned to the release it ships in, or left in the backlog.
3. **(If `needs-design`)** — write the design doc / wiki page first; implementation issue follows.
4. **PR** — work happens on a branch; the PR closes the issue (see below).

**Epics** (`type:epic`) use GitHub task lists (`- [ ] #N`) to track their child issues; children close individually, the epic closes when the list is done.

## PR conventions

- **Branch naming:** `feature/<area>-<slug>` (e.g. `feature/audio-miniaudio-core`, `feature/rendering-spot-shadows`).
- **Title:** `area: description` (e.g. `audio: vendor miniaudio + native AudioEngine core`).
- **EVERY PR links its issue** with `Closes #N` in the body — the PR template enforces it. No orphan PRs.
- One issue per PR where practical; epics are never closed by a single PR.

### Definition of done

A PR is mergeable when:

1. **Editor smoke test** — editor launches, project opens, the touched feature works, no new console errors.
2. **F12 capture for visual changes** — any rendering/UI-visible change includes an in-game F12 native back-buffer screenshot as evidence (the native game window cannot be GDI-captured; F12 is the supported path).
3. **Changelog entry** — the change is noted for the next release's changelog.

## How to build

- Open **`Vortex.sln`** in **Visual Studio 2022** (v143 toolset + .NET). Build the full solution — the native engine, VortexAPI, and the WPF editor are separate layers that must all be current (a stale native build shows as a white viewport).
- **Never build the editor via `dotnet build` on the csproj** — the Editor project is a **non-SDK-style csproj** and this is a known trap. Always go through the solution/MSBuild.
- Check the repository **README** for full build details (prerequisites, submodules such as `ThirdParty/Streamline` and `Templates/Default3D`, and the Streamline/DLSS prebuild step).

See also: [[Design-Claude-Integration]] · [[Performance-Master-Plan]]
