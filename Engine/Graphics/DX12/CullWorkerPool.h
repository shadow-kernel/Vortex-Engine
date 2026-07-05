#pragma once

#include "../../Common/CommonHeaders.h"
#include <thread>
#include <mutex>
#include <condition_variable>
#include <functional>
#include <vector>

namespace vortex::graphics::dx12
{
	// Persistent worker pool for the per-frame parallel cull/pack.
	//
	// The MT cull used to spawn + join FRESH std::threads every frame (up to 3 dispatch sites per
	// frame). Beyond the ~30-60µs creation cost per thread, every thread create/exit raises a Win32
	// debug event that suspends the whole process and round-trips to the debugger — under a VS
	// mixed-mode F5 that alone (~28 events/frame) dropped the editor viewport to a slideshow while
	// the same Debug build ran fine standalone. Workers are created once (lazily, on the first
	// parallel dispatch) and sleep on a condition variable between dispatches.
	class CullWorkerPool
	{
	public:
		~CullWorkerPool() { shutdown(); }

		// Run fn(begin, end) over [0, count) split across `workers` threads total — the CALLING
		// thread executes the first slice, pool threads take the rest. Blocks until all of [0, count)
		// is processed. fn must be safe to run concurrently on disjoint ranges (same contract as the
		// old per-frame thread splits, which this replaces slice-for-slice).
		void run(u32 workers, size_t count, const std::function<void(size_t, size_t)>& fn)
		{
			if (count == 0) return;
			if (workers < 2) { fn(0, count); return; }
			ensure(workers - 1);

			const size_t per = (count + workers - 1) / workers;
			u32 slices;
			{
				std::lock_guard<std::mutex> lk(m_mutex);
				m_fn = &fn;
				m_count = count;
				m_per = per;
				slices = (u32)((count + per - 1) / per);
				m_next_slice = 1;             // slice 0 belongs to the caller
				m_pending = slices - 1;
				++m_generation;
			}
			if (slices > 1) m_wake.notify_all();

			fn(0, per < count ? per : count);

			if (slices > 1)
			{
				std::unique_lock<std::mutex> lk(m_mutex);
				m_done.wait(lk, [this] { return m_pending == 0; });
				m_fn = nullptr;
			}
		}

		void shutdown()
		{
			{
				std::lock_guard<std::mutex> lk(m_mutex);
				m_quit = true;
				++m_generation;
			}
			m_wake.notify_all();
			for (auto& t : m_threads)
				if (t.joinable()) t.join();
			m_threads.clear();
			m_quit = false;
		}

	private:
		void ensure(u32 helper_count)
		{
			while (m_threads.size() < helper_count)
				m_threads.emplace_back([this] { worker_loop(); });
		}

		void worker_loop()
		{
			u64 seen = 0;
			std::unique_lock<std::mutex> lk(m_mutex);
			for (;;)
			{
				m_wake.wait(lk, [&] { return m_quit || m_generation != seen; });
				if (m_quit) return;
				seen = m_generation;

				// Pull slices until this dispatch is drained (lock dropped while running fn).
				while (m_fn && m_next_slice * m_per < m_count)
				{
					const size_t a = (size_t)m_next_slice++ * m_per;
					const size_t b = (a + m_per < m_count) ? a + m_per : m_count;
					const std::function<void(size_t, size_t)>* fn = m_fn;
					lk.unlock();
					(*fn)(a, b);
					lk.lock();
					if (--m_pending == 0) m_done.notify_one();
				}
			}
		}

		std::vector<std::thread> m_threads;
		std::mutex m_mutex;
		std::condition_variable m_wake, m_done;
		const std::function<void(size_t, size_t)>* m_fn{ nullptr };
		size_t m_count{ 0 };
		size_t m_per{ 0 };
		u32 m_next_slice{ 0 };
		u32 m_pending{ 0 };
		u64 m_generation{ 0 };
		bool m_quit{ false };
	};
}
