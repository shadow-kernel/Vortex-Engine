#pragma once

// Shared include block for the ResourceRegistry implementation, split across concern-grouped .cpp files
// (ResourceRegistry.cpp + ResourceRegistry_*.cpp). All implement the SAME ResourceRegistry class via C++
// partial implementation; this header just gives each the common includes.
#include "ResourceRegistry.h"
#include "../Geometry/MeshGeneratorFactory.h"
#include "../Geometry/MeshDecimator.h"
#include "../Importers/ModelImporter.h"
#include "../Importers/TextureImporter.h"
#include "../Importers/MeshSerializer.h"
#include <algorithm>
#include <cmath>
#include <string>
#include <cfloat>
#include <Windows.h>
