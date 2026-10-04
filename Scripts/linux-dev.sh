#!/usr/bin/env bash
# Vortex Engine — Linux development build.
#
#   Scripts/linux-dev.sh                 build the native engine (Debug) + the .NET layer
#   Scripts/linux-dev.sh --release       the optimised twin
#   Scripts/linux-dev.sh --test          build, then run the native smoke tests
#   Scripts/linux-dev.sh --editor        build, then start the editor
#   Scripts/linux-dev.sh --player [DIR]  build, then start the player on a project (default: Templates/Default3D)
#
# The native build produces build/linux-<cfg>/bin: libVortexAPI.so, the smoke tests and Shaders/spirv (the
# GLSL set compiled to SPIR-V by glslc). The managed binaries find the newest build tree by themselves.
set -euo pipefail

self="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo"

config=debug
run=""
project=""
while [[ $# -gt 0 ]]; do
	case "$1" in
		--release) config=release ;;
		--debug)   config=debug ;;
		--test)    run=test ;;
		--editor)  run=editor ;;
		--player)  run=player; if [[ ${2-} && ${2-} != --* ]]; then project="$2"; shift; fi ;;
		-h|--help) sed -n '2,10p' "$self" | sed 's/^# \{0,1\}//'; exit 0 ;;
		*) echo "unknown option: $1" >&2; exit 2 ;;
	esac
	shift
done
preset="linux-$config"
dotnet_config=$([[ $config == release ]] && echo Release || echo Debug)

# ---- prerequisites ----------------------------------------------------------
missing=()
for tool in cmake ninja glslc dotnet; do command -v "$tool" >/dev/null || missing+=("$tool"); done
if ! pkg-config --exists sdl3 2>/dev/null && [[ ! -f /usr/include/SDL3/SDL.h ]]; then missing+=("sdl3"); fi
if [[ ${#missing[@]} -gt 0 ]]; then
	echo "Missing: ${missing[*]}" >&2
	echo >&2
	echo "  Arch Linux:     sudo pacman -S --needed cmake ninja shaderc sdl3 assimp dotnet-sdk vulkan-icd-loader" >&2
	echo "  Debian/Ubuntu:  sudo apt install cmake ninja-build glslc libsdl3-dev libassimp-dev dotnet-sdk-10.0 libvulkan1" >&2
	echo "  Fedora:         sudo dnf install cmake ninja-build glslc SDL3-devel assimp-devel dotnet-sdk-10.0 vulkan-loader" >&2
	exit 1
fi

# ---- project templates (Git LFS) --------------------------------------------
# The templates keep their scenes, models, textures and audio in Git LFS. A clone made without LFS (or with
# GIT_LFS_SKIP_SMUDGE set) leaves ~130-byte pointer files behind, and a project created from such a template
# opens with an empty hierarchy and an empty viewport — which looks exactly like a broken renderer.
if [[ -d Templates ]]; then
	pointers=$(grep -rl --include='*' 'git-lfs.github.com/spec' Templates 2>/dev/null | wc -l)
	if [[ "$pointers" -gt 0 ]]; then
		echo "WARNING: $pointers template files are unfetched Git LFS pointers." >&2
		if command -v git-lfs >/dev/null; then
			echo "         fetching them now (git submodule foreach git lfs pull)..." >&2
			git submodule foreach 'git lfs pull' >/dev/null 2>&1 || true
			left=$(grep -rl --include='*' 'git-lfs.github.com/spec' Templates 2>/dev/null | wc -l)
			if [[ "$left" -gt 0 ]]; then
				echo "         $left still unresolved — check your network / LFS quota." >&2
			else
				echo "         done." >&2
			fi
		else
			echo "         Install git-lfs and run: git lfs install && git submodule foreach 'git lfs pull'" >&2
		fi
	fi
fi

# ---- native engine ----------------------------------------------------------
echo "==> native engine ($preset)"
cmake --preset "$preset"
cmake --build --preset "$preset" -j"$(nproc)"

# ---- managed layer ----------------------------------------------------------
echo "==> .NET layer ($dotnet_config)"
dotnet build Managed/Vortex.Managed.slnx -c "$dotnet_config" --nologo -v minimal

bin="$repo/build/$preset/bin"
editor="$repo/Managed/Vortex.Editor/bin/$dotnet_config/net10.0/Vortex.Editor"
player="$repo/Managed/Vortex.Player/bin/$dotnet_config/net10.0/Vortex.Player"

case "$run" in
	test)
		echo "==> smoke tests"
		ctest --preset "$preset"
		echo "==> render test (needs a display)"
		( cd "$bin" && ./VortexRenderTest )
		;;
	editor) echo "==> editor"; exec "$editor" ;;
	player)
		echo "==> player"
		exec "$player" --project="${project:-$repo/Templates/Default3D}" --scene=Match
		;;
	*)
		echo
		echo "Built. Start it with:"
		echo "  $editor"
		echo "  $player --project=Templates/Default3D --scene=Match"
		;;
esac
