# Vortex Engine — Roadmap (updated 2026-09-30)

Status legend: ✅ shipped on `main` · 🟡 partial · ⬜ open. Issue numbers refer to github.com/shadow-kernel/Vortex-Engine.

## Shipped in 2.8 (this merge)
- ✅ **macOS port** (#180): Metal backend via SDL GPU, .NET 10 `Vortex.Core` / `Vortex.Player` / Avalonia `Vortex.Editor`,
  `tools/macos/make-app.sh --install --dmg` builds and installs `Vortex Editor.app`; branded exports for macOS /
  Windows / Linux with runtime packs (`tools/make-runtime-pack.*`).
- ✅ **Physics v2 — Jolt** (#101, #102, 🟡 #105 #106 #107): `Engine/Physics/PhysicsWorld` + 41 C-ABI exports, managed
  `PhysicsService` (60 Hz fixed step, transform write-back, contact events → `OnCollisionEnter/OnTrigger*`), script API
  `Physics.AddForce/AddImpulse(AtPoint)/AddTorque/SetVelocity/OverlapSphere/…`, compound shapes, physics materials,
  debug lines, CharacterVirtual API. The Visual Studio build compiles the stub until Jolt is added to the .vcxproj.
- ✅ **Character controller fixes**: edge pops / one-frame drops on curbs, step-up at high frame rates, jump swallowed by
  the step-up settle; camera step smoothing; raycast mantle in the template.
- ✅ **Editor-first hands** (🟡 #147): `HandPose` component (per-finger curl, live preview), weapon sockets as child
  entities (`Grip`, `SupportGrip`, `Sight`, `Muzzle`, `Eject`, `MagWell`), auto-grip from the rig's hand geometry.
- ✅ **Horror Starter**: open night map "Yard" (33 Poly Haven CC0 props, 64 dynamic bodies), CoD movement (speeds,
  sprint-out, no run-and-gun, R6 lean, ADS toggle), recorded weapon sounds in random containers, `Player.ventity` and
  `Character_Soldier.ventity` blueprints.

## Next (in order)
1. **Physics follow-ups** — ⬜ #103 constraints (hinge doors, ball, slider, fixed), ⬜ #104 ragdoll, 🟡 #105 move the
   player onto `CharacterVirtual`, render interpolation between fixed steps, Jolt in the Visual Studio build.
2. **Windows parity of the port** — Avalonia editor on Windows (HWND viewport), CI runner for the macOS build
   (`.github/workflows/build-macos.yml`), Developer-ID signing + notarisation (#96).
3. **Animation** — foot placement + look-at (#147 rest), state machine / blend-tree editor (#146), retargeting (#148),
   root motion (#113).
4. **VFX** — GPU particles (#116/#117/#118), decals (#120), trails (#121) → weapon VFX package (#178).
5. **AI & Navigation** — NavMesh (#109), agents (#110), perception (#112), behaviour trees (#111), horror monster sample (#115).
6. **Asset Store / Library** — provider abstraction + Poly Haven / ambientCG providers (#66–#69), global asset DB (#52–#65).
7. **Rendering** — TAA (#141), profiler (#144), volumetric fog (#119), reflection probes (#167), GPU-driven renderer (#131…).
8. **Claude in the editor** — MCP server + tool sets (#84–#94), Sound Studio (#78–#83, #99).
9. **Release engineering** — automated test gate (#161), nightly (#160), crash reporting (#97), v3.0 hardening (#95).
