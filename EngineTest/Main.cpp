#pragma comment(lib, "Engine.lib")

#define TEST_ECS 1

#if TEST_ECS
#include "TestECS.h"
#else
#error "No test defined"
#endif

int main() 
{

#if _DEBUG
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