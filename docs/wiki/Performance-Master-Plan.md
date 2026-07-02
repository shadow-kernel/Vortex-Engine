# Performance Master Plan — the 10x Path

The endgame is **[milestone 10 — v4.0.0 XXL: 10x Performance](https://github.com/shadow-kernel/Vortex-Engine/milestone/10)**: a GPU-driven, UE5-class renderer. Groundwork lands earlier in **[milestone 9 — v3.4.0 World & Streaming](https://github.com/shadow-kernel/Vortex-Engine/milestone/9)**. Track work under the [`type:perf` label](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Atype%3Aperf).

## Verified baseline (v2.5.x)

- **Native GameHost** (dedicated Win32 thread: window + message pump + render loop, `DXGI_PRESENT_ALLOW_TEARING`, flip-model, submit-once path for static scenes): ~**1850 FPS** on simple match scenes, ~1280 FPS lobby (was 60 under WPF HwndHost).
- **63k instances @ 240 FPS** with 2-pass multithreaded culling + geometric LOD.
- **DLSS 4 Super-Resolution + Frame Generation** (x2/x3/x4) via NVIDIA Streamline, with Reflex latency markers; graceful fallback to bilinear upscale.
- Fixed 1/60s timestep runtime; render-scale pipeline [0.25, 2.0] with offscreen RT → upscale slot.

## What exists vs. what's missing (perf scan)

### Already implemented

| Tech | State |
|---|---|
| GPU instancing | Per-instance world matrices in persistent instance buffer; sorted by (material, mesh, distance) → one `DrawIndexedInstanced` per run; max 262k instances, 8192 draw runs |
| 2-pass MT culling | Pass A: parallel frustum/LOD test → per-LOD counters; prefix-sum compact; Pass B: parallel pack into compacted slabs; lock-free atomics |
| Geometric LOD | Per-mesh LOD chains (up to 4 levels) via vertex-cluster decimation at import; distance buckets; contiguous slab per LOD |
| Pre-cull | Parallel O(n) frustum cull before the O(n log n) sort when queue > 262k instances |
| Distance + density culling | Render-distance gate; density LOD thins distant instances 1/2 and 1/4 |
| Render scale + DLSS SR/FG | Scaled RT → upscale/DLSS slot → UI composite; motion-vector pass (RG16F, camera reprojection) |
| GPU skinning | Bone palettes in dual-half persistently-mapped buffer (64KB+64KB, no torn reads); skinned PSO |
| Deferred resize / scene switch | No Present freeze; raw-input mouse (no cursor repositioning bottleneck) |
| Telemetry | FPS, draw calls, vertex count, instances tested/drawn per frame |

### Missing (verified gaps)

- **Occlusion culling** — no HZB/Z-pyramid; indoor scenes render everything behind walls (frustum + distance are the only culls).
- **Render graph** — hard-coded pass sequence (scene → mvec → upscale → UI); no pass culling/reordering.
- **GPU-driven rendering** — no ExecuteIndirect; draw submission is CPU-side.
- **Bindless** — static SRV heap slots; root signature recompiled per custom material.
- **Mesh shaders**, **async compute** (single queue, zero compute shaders today), **virtual texturing / sampler feedback**.
- **Job system** — 2-pass cull spawns `std::thread` per frame; no persistent work-stealing pool.
- **ECS/data-oriented core** — OOP RenderItem + STL sort; no arenas (standard new/delete in hot paths).
- **Spatial partition** — culling is O(all instances); no octree/BVH.
- **TAA** — no AA at all outside DLSS; motion vectors are camera-only (skinned meshes ghost under FG); no skeletal LOD (decimator drops bone weights).

## Staged path

### Stage 1 — v3.4.0 World & Streaming ([milestone 9](https://github.com/shadow-kernel/Vortex-Engine/milestone/9))

1. **HZB occlusion culling** — Hi-Z pyramid from previous-frame depth + conservative instance tests inside the existing 2-pass MT cull. Huge win for indoor horror scenes.
2. **Async asset streaming** — background-thread mip/mesh loading, residency states, distance priority; groundwork for virtual texturing.
3. **Spatial partition (octree/BVH)** — accelerates frustum/occlusion tests, raycasts, and trigger overlap.
4. Level streaming volumes + async scene-chunk loading (per-scene `.vpak` MountLayer/UnmountLayer already exists).

### Stage 2 — v4.0.0 XXL ([milestone 10](https://github.com/shadow-kernel/Vortex-Engine/milestone/10))

**Sequence deliberately: render graph FIRST** — everything else plugs into it.

| # | Feature | Expected win | Depends on |
|---|---|---|---|
| 1 | **Render graph / frame graph** — declarative passes, auto barriers, transient RT pooling, pass culling | Enabler (correctness + free pass culling) | — |
| 2 | **Bindless resources** — unbounded SRV heap + material index buffer | Removes per-draw descriptor binding; fixes root-sig-per-custom-material trap | Render graph |
| 3 | **GPU culling + ExecuteIndirect** — cull/LOD-select in compute, indirect args, one ExecuteIndirect per pass | ~**10x** on high-instance scenes (CPU draw submission stops scaling with instance count) | Bindless, HZB (v3.4) |
| 4 | **Mesh shaders (meshlets)** — meshlet build at import, task/mesh path, per-meshlet cone culling | Finer-grain culling; classic-path fallback for older GPUs | GPU culling |
| 5 | **Job system** — persistent work-stealing pool + job graph | Kills per-frame thread-spawn overhead; parallelizes cull → animation → streaming | — |
| 6 | **ECS / data-oriented scene core** — archetype/SoA, dirty tracking, parallel systems | Cache-coherent scene updates at scale | Job system; design doc first (`needs-design`) |
| 7 | **Frame allocators & arenas** — linear per-frame arenas, upload ring buffers | Removes new/delete from hot paths | — |
| 8 | **Async compute** — second D3D12 queue (particles/HZB/AO/skinning overlap graphics) | Hides compute latency | Render graph barriers |
| 9 | **Virtual texturing / sampler feedback** — sparse-residency mega-textures | Unlimited texture worlds on fixed VRAM | Async streaming (v3.4) |
| 10 | **TAA + temporal history** — jitter + history reprojection on existing motion vectors | First non-DLSS AA; unlocks temporal SSR/AO later | — |
| 11 | **Skinned LOD + per-object motion vectors** — weight-preserving decimation, skinned prev-pose mvec pass | Fixes DLSS-FG ghosting on characters; skeletal LOD | — |

## Benchmark methodology

The guardrail that makes "10x" provable ([`area:build-ci`](https://github.com/shadow-kernel/Vortex-Engine/issues?q=is%3Aissue+label%3Aarea%3Abuild-ci)):

- **Headless CI benchmark scenes:** instancing storm, indoor occlusion, skinned crowd, particle storm.
- Each run produces **frame-time JSON**; CI **fails the PR on >X% regression**; history chart kept in the repo.
- Today only the editor-manual StressTestDialog exists (spawn N copies, live FPS/draw-call stats, `--stress`/`--benchmark` process modes) — the CI harness is new work in milestone 10.
- The v4.0 epic defines the target scenes + metrics against the verified baseline above.

See also: [[Design-Claude-Integration]] · [[Contributing-Workflow]]
