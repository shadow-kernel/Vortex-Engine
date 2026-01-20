#include "DX12CommandQueue.h"

namespace vortex::graphics::dx12
{
	bool DX12CommandQueue::initialize(ID3D12Device* device)
	{
		if (!device) return false;

		D3D12_COMMAND_QUEUE_DESC desc{};
		desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
		desc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;

		if (FAILED(device->CreateCommandQueue(&desc, IID_PPV_ARGS(&m_queue))))
			return false;

		if (FAILED(device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&m_fence))))
			return false;

		m_fence_event = CreateEvent(nullptr, FALSE, FALSE, nullptr);
		if (!m_fence_event) return false;

		m_fence_value = 0;
		return true;
	}

	void DX12CommandQueue::shutdown()
	{
		flush();
		if (m_fence_event)
		{
			CloseHandle(m_fence_event);
			m_fence_event = nullptr;
		}
		m_fence.Reset();
		m_queue.Reset();
	}

	void DX12CommandQueue::execute_command_list(ID3D12CommandList* list)
	{
		ID3D12CommandList* lists[] = { list };
		m_queue->ExecuteCommandLists(1, lists);
	}

	void DX12CommandQueue::flush()
	{
		signal_and_wait();
	}

	void DX12CommandQueue::signal_and_wait()
	{
		++m_fence_value;
		m_queue->Signal(m_fence.Get(), m_fence_value);

		if (m_fence->GetCompletedValue() < m_fence_value)
		{
			m_fence->SetEventOnCompletion(m_fence_value, m_fence_event);
			WaitForSingleObject(m_fence_event, INFINITE);
		}
	}
}
