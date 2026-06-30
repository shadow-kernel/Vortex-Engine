#pragma once

// Shared include block for the DX12Renderer implementation, which is split across several concern-grouped
// .cpp files (DX12Renderer.cpp + DX12Renderer_*.cpp). They all implement methods of the SAME DX12Renderer
// class (C++ partial implementation across translation units) — this header just gives each the common
// includes so the split files stay small + readable without duplicating the include list.
#include "DX12Renderer.h"
#include "DX12Streamline.h"
#include "../Resources/ResourceRegistry.h"
#include <algorithm>
#include <memory>
#include <thread>
#include <atomic>
#include <unordered_map>
