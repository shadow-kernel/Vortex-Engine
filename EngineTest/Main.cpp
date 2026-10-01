#ifdef _MSC_VER
#pragma comment(lib, "Engine.lib")
#include <crtdbg.h>
#endif

// Pick exactly one test. TEST_AUDIO is the non-interactive smoke test and the
// default; TEST_ECS is the original interactive ECS stress test; TEST_RENDER opens
// a native window, renders a lit scene and captures it (CMake builds it as VortexRenderTest);
// TEST_PHYSICS is the non-interactive Jolt smoke test (CMake builds it as VortexPhysicsTest);
// TEST_PARTICLES the particle (VFX) test (VortexParticleTest); TEST_NAVIGATION the Recast/Detour test (VortexNavigationTest).
#if !defined(TEST_AUDIO) && !defined(TEST_ECS) && !defined(TEST_RENDER) && !defined(TEST_PHYSICS) && !defined(TEST_PARTICLES) && !defined(TEST_NAVIGATION)
#define TEST_AUDIO 1
#endif
#ifndef TEST_AUDIO
#define TEST_AUDIO 0
#endif
#ifndef TEST_ECS
#define TEST_ECS 0
#endif
#ifndef TEST_RENDER
#define TEST_RENDER 0
#endif
#ifndef TEST_PHYSICS
#define TEST_PHYSICS 0
#endif
#ifndef TEST_PARTICLES
#define TEST_PARTICLES 0
#endif
#ifndef TEST_NAVIGATION
#define TEST_NAVIGATION 0
#endif

#if TEST_AUDIO
#include "TestAudio.h"
#elif TEST_ECS
#include "TestECS.h"
#elif TEST_RENDER
#include "TestRender.h"
#elif TEST_PHYSICS
#include "TestPhysics.h"
#elif TEST_PARTICLES
#include "TestParticles.h"
#elif TEST_NAVIGATION
#include "TestNavigation.h"
#else
#error "No test defined"
#endif

int main()
{

#if defined(_DEBUG) && defined(_MSC_VER)
	_CrtSetDbgFlag(_CRTDBG_ALLOC_MEM_DF | _CRTDBG_LEAK_CHECK_DF);
#endif

	engine_test test_instance{};

	if (test_instance.initialize())
	{
		test_instance.run();
	}

	test_instance.shutdown();
	return 0;
}
