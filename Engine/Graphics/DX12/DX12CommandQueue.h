#pragma once

#include "../../Common/CommonHeaders.h"
#include <d3d12.h>
#include <wrl/client.h>

namespace vortex::graphics::dx12
{
	using Microsoft::WRL::ComPtr;

	class DX12CommandQueue
	{
	public:
		bool initialize(ID3D12Device* device);
		void shutdown();

		void execute_command_list(ID3D12CommandList* list);
		void flush();
		void signal_and_wait();
		
		// Non-blocking signal - returns fence value to wait on later
		UINT64 signal();
		// Wait for a specific fence value if not yet completed
		void wait_for_fence_value(UINT64 fence_value);
		// Check if a fence value has been reached
		bool is_fence_complete(UINT64 fence_value) const;

		ID3D12CommandQueue* queue() const { return m_queue.Get(); }
		UINT64 current_fence_value() const { return m_fence_value; }

	private:
		ComPtr<ID3D12CommandQueue> m_queue;
		ComPtr<ID3D12Fence> m_fence;
		HANDLE m_fence_event{ nullptr };
		UINT64 m_fence_value{ 0 };
	};
}
