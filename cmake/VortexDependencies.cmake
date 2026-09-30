# Third-party dependencies of the native engine. Each one becomes an INTERFACE target the engine links:
#   vortex::directxmath   header-only math (Windows SDK on Windows, fetched release elsewhere + SAL stub)
#   vortex::assimp        model importer (vendored MSVC binaries on Windows, package-manager build elsewhere)
#   vortex::steamaudio    phonon headers only — the runtime library is dlopen'ed on demand
include(FetchContent)

# Homebrew on Apple Silicon lives in /opt/homebrew, which CMake does not search by default.
if(APPLE)
	execute_process(COMMAND brew --prefix
		OUTPUT_VARIABLE VORTEX_BREW_PREFIX OUTPUT_STRIP_TRAILING_WHITESPACE
		RESULT_VARIABLE _vortex_brew_result ERROR_QUIET)
	if(_vortex_brew_result EQUAL 0 AND VORTEX_BREW_PREFIX)
		list(APPEND CMAKE_PREFIX_PATH "${VORTEX_BREW_PREFIX}")
	endif()
endif()

find_package(Threads REQUIRED)

# ---- DirectXMath ------------------------------------------------------------
add_library(vortex_directxmath INTERFACE)
add_library(vortex::directxmath ALIAS vortex_directxmath)
if(NOT WIN32)
	# Pinned release tarball of microsoft/DirectXMath (MIT). Header-only, so its own CMakeLists is skipped
	# (SOURCE_SUBDIR points at a folder without one) and Inc/ is used directly.
	FetchContent_Declare(directxmath
		URL      https://github.com/microsoft/DirectXMath/archive/refs/tags/may2026.tar.gz
		URL_HASH SHA256=be483d56cdc7be10b28111e81ebfca83cc1048d99e5b3041555dae26c66aaa32
		DOWNLOAD_EXTRACT_TIMESTAMP TRUE
		SOURCE_SUBDIR headers-only)
	FetchContent_MakeAvailable(directxmath)
	target_include_directories(vortex_directxmath INTERFACE
		"${directxmath_SOURCE_DIR}/Inc"
		"${CMAKE_SOURCE_DIR}/ThirdParty/sal")   # SAL annotation stub (the Windows SDK provides the real one)
endif()

# ---- Assimp -------------------------------------------------------------------
add_library(vortex_assimp INTERFACE)
add_library(vortex::assimp ALIAS vortex_assimp)
if(WIN32)
	# The same prebuilt Assimp 6 (MSVC v143, x64) the Visual Studio projects use.
	set(_assimp_root "${CMAKE_SOURCE_DIR}/ThirdParty/assimp6")
	add_library(vortex_assimp_prebuilt SHARED IMPORTED)
	set_target_properties(vortex_assimp_prebuilt PROPERTIES
		IMPORTED_LOCATION "${_assimp_root}/bin/assimp-vc143-mt.dll"
		IMPORTED_IMPLIB   "${_assimp_root}/lib/assimp-vc143-mt.lib"
		INTERFACE_INCLUDE_DIRECTORIES "${_assimp_root}/include")
	target_link_libraries(vortex_assimp INTERFACE vortex_assimp_prebuilt)
else()
	# macOS: brew install assimp   /   Linux: apt install libassimp-dev (or vcpkg)
	find_package(assimp CONFIG REQUIRED)
	# Homebrew bottles record the absolute SDK path of the machine that built them
	# (/Library/Developer/CommandLineTools/SDKs/MacOSX<ver>.sdk/usr/lib/libz.tbd). On a machine with a
	# different SDK layout that file does not exist and ninja refuses to link. Map such stale absolute
	# system-library paths back to plain -l<name> so the local SDK resolves them.
	get_target_property(_assimp_link_libs assimp::assimp INTERFACE_LINK_LIBRARIES)
	if(_assimp_link_libs)
		set(_assimp_fixed_libs "")
		foreach(_lib IN LISTS _assimp_link_libs)
			# Entries may be wrapped in generator expressions: $<$<CONFIG:DEBUG>:/path/to/libz.tbd>
			string(REGEX MATCH "/[^;<>$]*/lib([A-Za-z0-9_+.-]+)\\.(tbd|dylib|so)" _lib_path "${_lib}")
			if(_lib_path AND NOT EXISTS "${_lib_path}")
				set(_lib_name "${CMAKE_MATCH_1}")
				message(STATUS "assimp: replacing stale link path ${_lib_path} with -l${_lib_name}")
				string(REPLACE "${_lib_path}" "${_lib_name}" _lib "${_lib}")
			endif()
			list(APPEND _assimp_fixed_libs "${_lib}")
		endforeach()
		set_target_properties(assimp::assimp PROPERTIES INTERFACE_LINK_LIBRARIES "${_assimp_fixed_libs}")
	endif()
	target_link_libraries(vortex_assimp INTERFACE assimp::assimp)
endif()

# ---- Steam Audio (headers only) ------------------------------------------------
add_library(vortex_steamaudio INTERFACE)
add_library(vortex::steamaudio ALIAS vortex_steamaudio)
target_include_directories(vortex_steamaudio INTERFACE "${CMAKE_SOURCE_DIR}/ThirdParty/steam-audio/include")
