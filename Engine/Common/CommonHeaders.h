#pragma once

#include "Platform.h"

#ifdef _MSC_VER
#pragma warning(disable: 4530)
#endif

#include <stdint.h>
#include <assert.h>
#include <typeinfo>

// DirectXMath is the engine's math library on every platform: header-only, MIT licensed, part of the
// Windows SDK and buildable with clang/gcc (SSE on x64, NEON on Apple Silicon). The CMake build fetches
// it (plus a SAL stub, see ThirdParty/sal) on non-Windows platforms.
#include <DirectXMath.h>

#include "../Utilities/Utilities.h"
#include "PrimitiveTypes.h"
#include "VortexAssert.h"
