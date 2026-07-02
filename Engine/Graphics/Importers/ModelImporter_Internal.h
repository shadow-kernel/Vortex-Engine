#pragma once

#include "ModelImporter.h"

#ifdef VORTEX_USE_ASSIMP
#include <assimp/Importer.hpp>
#include <assimp/scene.h>
#include <assimp/postprocess.h>
#include <assimp/config.h>   // AI_CONFIG_* importer property keys (FBX pivot collapsing)
#endif

#include <algorithm>
#include <limits>
#include <cstdio>
#include <cctype>
#include <Windows.h>
