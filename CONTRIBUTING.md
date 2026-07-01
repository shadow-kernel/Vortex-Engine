# Contributing to Vortex Engine

Thanks for your interest in Vortex! 🌀 Vortex is a free, open-source (MIT) game
engine and **everyone is welcome to contribute** — bug fixes, features, docs,
assets, or ideas.

## Ground rules

- Be kind and constructive. See the [Code of Conduct](CODE_OF_CONDUCT.md).
- **Gameplay lives in project scripts, not the engine.** Health, weapons,
  controllers, game rules, etc. belong in `VortexBehaviour` scripts in a
  project — never hardcoded into `Engine/` or the editor. Keep the engine
  generic and reusable.
- Keep changes focused. One logical change per pull request.

## Getting set up

**Prerequisites (Windows x64):**
- Visual Studio 2022 or 2026 with:
  - *Desktop development with C++* (MSVC v143/v145 + Windows 10/11 SDK)
  - *.NET desktop development* (.NET Framework 4.8 targeting pack)

```bash
# clone WITH submodules (the DLSS/Streamline SDK + the default 3D template are submodules)
git clone --recurse-submodules https://github.com/shadow-kernel/Vortex-Engine.git
cd Vortex-Engine
# (if you already cloned without --recurse-submodules)
git submodule update --init --recursive

# restore NuGet packages
nuget restore Engine/packages.config    -SolutionDirectory .
nuget restore VortexAPI/packages.config -SolutionDirectory .
nuget restore Editor/packages.config    -SolutionDirectory .

# build the whole solution (Engine -> VortexAPI.dll -> Editor)
msbuild Vortex.slnx /t:Build /p:Configuration=Release /p:Platform=x64

# run
"x64/Release/Vortex Engine.exe"
```

> DLSS is optional. Without the NVIDIA Streamline SDK present it simply stays
> off; the engine builds and runs normally.

## Architecture at a glance

Three layers (see the [README](README.md) for the full diagram):

- **`Engine/`** — C++20 / DirectX 12 core → `Engine.lib` (links into shipped games)
- **`VortexAPI/`** — thin `extern "C"` interop → `VortexAPI.dll`
- **`Editor/`** — C# / WPF authoring tool → `Vortex Engine.exe`

## Pull request flow

1. Fork the repo and create a branch: `git checkout -b my-change`.
2. Make your change; match the surrounding code style (indentation, naming, comment density).
3. Build `Release | x64` green and test in the editor.
4. Commit with a clear message describing the *why*.
5. Open a PR against `main` with a short description and screenshots/GIFs for UI changes.

## Reporting bugs / requesting features

Open an issue with clear steps to reproduce (bugs) or a concise description of
the use case (features). Please include your OS, GPU, and Vortex version.

## Licensing of contributions

By contributing, you agree that your contributions are licensed under the
project's [MIT License](LICENSE). Don't add third-party code or assets whose
license is incompatible with MIT, and credit any permissively-licensed
third-party material in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
