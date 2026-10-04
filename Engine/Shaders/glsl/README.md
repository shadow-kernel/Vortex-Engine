# GLSL shader set (Vulkan / SPIR-V)

The Vulkan twin of [`../msl`](../msl). Same math, same uniform byte layouts, same binding slots — only the
language and the file granularity differ.

## Why one file per entrypoint

Metal compiles MSL at load time, so `standard.metal` can hold `VSMain`, `VSSkinned`, `ShadowVS`, `PSMain` … and
SDL is told which entrypoint to use. A SPIR-V module carries exactly **one** entrypoint, called `main`, so each
entrypoint is its own file here and the build compiles it to the name the backend looks up:

```
standard.PSMain.frag   ->   Shaders/spirv/standard.PSMain.spv
grid.GridVS.vert       ->   Shaders/spirv/grid.GridVS.spv
```

`<base>.<Entry>.<stage>` — the stage lives in the source extension only (`glslc` infers it from `.vert` /
`.frag`). [`../../Graphics/SdlGpu/SdlGpuShaderFormat.h`](../../Graphics/SdlGpu/SdlGpuShaderFormat.h) builds the
same name at run time; call sites just say `create_shader("standard", "PSMain", …)`.

Shared declarations live in `include/` and are pulled in with `#include` (`-I include`, enabled by
`GL_GOOGLE_include_directive`).

## Rules the set has to follow

**Binding sets** — SDL GPU fixes these for SPIR-V (`SDL_CreateGPUShader`):

| stage | set 0 / 2 | set 1 / 3 |
|---|---|---|
| vertex | sampled textures → storage textures → storage buffers (**set 0**) | uniform buffers (**set 1**) |
| fragment | sampled textures → storage textures → storage buffers (**set 2**) | uniform buffers (**set 3**) |

The include files take the set number from a `SET_UNIFORM` macro so one header serves both stages.

**No coordinate flipping** — SDL GPU presents a single left-handed convention and converts for Vulkan's
+Y-down NDC behind the scenes, so every vertex shader emits exactly the clip position its `.metal` twin emits.
A `gl_Position.y = -gl_Position.y` in a fullscreen pass is that pass's own UV convention, not an NDC fix.

**std140 vs MSL packing** — a `vec3` takes a 16-byte slot but leaves its last 4 bytes to a following `float`,
which is exactly how MSL packs `packed_float3` + `float`: those pairs port verbatim. A **scalar array** does
not: std140 gives it a 16-byte stride, so MSL's tightly packed `uint pad[3]` / `float pad[3]` must be spelled
out as individual scalars. Getting this wrong silently shifts every later member.

The blocks are byte-matched to the C++ structs in `SdlGpuRenderer.h` / `SdlGpuParticles.cpp`, which carry
`static_assert`s on their sizes. To check a block after editing it:

```bash
glslc --target-env=vulkan1.0 -I include -o /tmp/s.spv standard.PSMain.frag
spirv-dis --no-color /tmp/s.spv | grep "MemberDecorate %PerFrameBlock"
```

**Comparison samplers** — the shadow atlases are sampled with a comparison sampler, so they are
`sampler2DShadow` and are read with `texture(atlas, vec3(uv, ref))`. Vulkan rejects a colour texture or a
non-comparison sampler in those slots, which is why the renderer keeps a 1×1 depth stand-in for when shadows
are off.

## Custom material shaders

`material_template.glsl` is what the editor copies when a project creates a shader asset. One file provides
both stages, guarded by `VORTEX_VERTEX_STAGE` / `VORTEX_FRAGMENT_STAGE`; the engine compiles it twice with
`glslc` when the material loads and again whenever the file changes. The build compiles it for both stages too,
so a broken template fails the build instead of the editor.
