#pragma once

#include "../Engine/Graphics/Particles/ParticleSystem.h"

#include "Test.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <string>
#include <vector>

using namespace vortex;

// VFX smoke test (epic #116: #117 core, #121 trails & beams, #122 collision): drives Engine/Graphics/Particles
// directly (the module libVortexAPI exports through ParticleApi.cpp). Headless (no GPU needed): the render side
// is exercised through gather(). Prints "ALL PASSED" or "FAILURES!" for ctest.
class engine_test : public test
{
public:
	bool initialize() override
	{
		std::cout << "Particle Test Initialized\n";
		return particles::init();
	}

	void run() override
	{
		check("init() is idempotent", particles::init());
		run_emission_tests();
		run_lifetime_tests();
		run_burst_tests();
		run_motion_tests();
		run_shape_tests();
		run_determinism_tests();
		run_json_tests();
		run_gather_tests();
		run_trail_and_beam_tests();
		run_collision_tests();
		run_handle_and_callback_tests();
		run_performance_test();
		std::cout << "\nRESULT: " << _passed << "/" << (_passed + _failed)
			<< (_failed == 0 ? " - ALL PASSED\n" : " - FAILURES!\n");
	}

	void shutdown() override { particles::shutdown(); }

private:
	int _passed{ 0 }, _failed{ 0 };
	static constexpr float DT = 1.0f / 60.0f;

	void check(const char* name, bool ok, const std::string& detail = std::string())
	{
		if (ok) ++_passed; else ++_failed;
		std::cout << (ok ? "[PASS] " : "[FAIL] ") << name;
		if (!detail.empty()) std::cout << "  (" << detail << ")";
		std::cout << "\n";
	}

	static std::string num(double v) { char b[64]; std::snprintf(b, sizeof(b), "%.4g", v); return b; }

	static particles::EmitterDesc base_desc()
	{
		particles::EmitterDesc d;
		particles::default_emitter_desc(d);
		d.shape = particles::SHAPE_POINT;
		d.speed[0] = d.speed[1] = 0.0f;
		d.seed = 1234;
		return d;
	}

	static void step(u32 world, int frames, float dt = DT) { for (int i = 0; i < frames; ++i) particles::update(world, dt); }

	// ---------------------------------------------------------------------------------------------------------
	void run_emission_tests()
	{
		std::cout << "\n-- emission --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.rate = 100.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		u32 h = particles::create_emitter(d, w);
		check("create_emitter returns a handle", h != 0);
		check("a new emitter is not playing", !particles::is_playing(h));
		step(w, 10);
		check("nothing spawns before play()", particles::alive_count(h) == 0);
		particles::play(h);
		step(w, 60);
		u32 n = particles::alive_count(h);
		check("rate 100/s for 1 s spawns 100 particles", n >= 99 && n <= 100, "alive " + std::to_string(n));
		step(w, 60);
		n = particles::alive_count(h);
		check("... and 200 after 2 s", n >= 199 && n <= 200, "alive " + std::to_string(n));

		// max particles caps the pool
		d.rate = 5000.0f; d.max_particles = 50;
		u32 capped = particles::create_emitter(d, w);
		particles::play(capped);
		step(w, 30);
		check("max_particles caps the alive count", particles::alive_count(capped) == 50, "alive " + std::to_string(particles::alive_count(capped)));

		// rate over distance
		d = base_desc(); d.rate = 0.0f; d.rate_over_distance = 8.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		u32 dist = particles::create_emitter(d, w);
		particles::play(dist);
		float m[16] = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
		// steps of 0.125 m (exact in binary); the first pose is the start, so 19 moves = 2.375 m -> 19 particles
		for (int i = 0; i < 20; ++i) { m[12] = 0.125f * (float)(i + 1); particles::set_transform(dist, m); particles::update(w, DT); }
		n = particles::alive_count(dist);
		check("rate over distance: 19 x 0.125 m at 8/m spawns 19", n == 19, "alive " + std::to_string(n));

		// start delay
		d = base_desc(); d.rate = 100.0f; d.start_delay = 0.5f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		u32 delayed = particles::create_emitter(d, w);
		particles::play(delayed);
		step(w, 24);
		check("start delay: nothing during the delay", particles::alive_count(delayed) == 0);
		step(w, 36);
		n = particles::alive_count(delayed);
		check("start delay: emits after it", n >= 49 && n <= 51, "alive " + std::to_string(n));

		// stop keeps particles, stop(clear) removes them
		particles::stop(h, false);
		u32 before = particles::alive_count(h);
		step(w, 10);
		check("stop(): no new particles, old ones live on", particles::alive_count(h) == before && !particles::is_playing(h));
		particles::stop(h, true);
		check("stop(clear): particles removed", particles::alive_count(h) == 0);
		particles::clear(w);
		check("clear(world) destroys its emitters", particles::emitter_count(w) == 0 && !particles::emitter_valid(h));
	}

	void run_lifetime_tests()
	{
		std::cout << "\n-- lifetime --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.rate = 100.0f; d.lifetime[0] = d.lifetime[1] = 0.5f;
		u32 h = particles::create_emitter(d, w);
		particles::play(h);
		step(w, 120);
		u32 n = particles::alive_count(h);
		check("steady state = rate x lifetime (100 x 0.5 = 50)", n >= 49 && n <= 51, "alive " + std::to_string(n));
		particles::stop(h, false);
		step(w, 36);
		check("all particles die within their lifetime after stop", particles::alive_count(h) == 0);
		check("is_finished after the last particle died", particles::is_finished(h));

		// non-looping: duration ends emission, auto-destroy cleans up
		particles::clear(w);
		d = base_desc();
		d.looping = 0; d.duration = 0.5f; d.rate = 100.0f; d.lifetime[0] = d.lifetime[1] = 0.25f;
		u32 once = particles::create_emitter(d, w);
		particles::set_auto_destroy(once, true);
		particles::play(once);
		step(w, 30);
		particles::Stats st{};
		particles::get_stats(w, st);
		check("non-looping: stops emitting after its duration", !particles::is_playing(once) && particles::emitter_valid(once));
		check("non-looping: spawned rate x duration", st.spawned_total >= 49 && st.spawned_total <= 51, "spawned " + std::to_string(st.spawned_total));
		step(w, 20);
		check("auto-destroy: the finished one-shot is gone", !particles::emitter_valid(once));

		// looping restarts the cycle and keeps emitting
		d = base_desc(); d.looping = 1; d.duration = 0.2f; d.rate = 50.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		u32 loop = particles::create_emitter(d, w);
		particles::play(loop);
		step(w, 60);
		n = particles::alive_count(loop);
		check("looping keeps emitting across cycles", n >= 49 && n <= 51 && particles::is_playing(loop), "alive " + std::to_string(n));

		// random lifetime within range
		d = base_desc(); d.rate = 0.0f; d.lifetime[0] = 0.2f; d.lifetime[1] = 0.6f;
		u32 r = particles::create_emitter(d, w);
		particles::burst(r, 1000);
		step(w, 12);   // 0.2 s: nothing dead yet (min lifetime)
		u32 a = particles::alive_count(r);
		step(w, 12);   // 0.4 s: about half
		u32 b = particles::alive_count(r);
		step(w, 13);   // > 0.6 s: all dead
		u32 c = particles::alive_count(r);
		check("random lifetime [0.2, 0.6]: none dies before 0.2 s", a >= 990, std::to_string(a));
		check("... about half gone at 0.4 s", b > 350 && b < 650, std::to_string(b));
		check("... all gone after 0.6 s", c == 0, std::to_string(c));
		particles::clear(w);
	}

	void run_burst_tests()
	{
		std::cout << "\n-- bursts --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		d.burst_count = 1;
		d.bursts[0] = particles::Burst{ 0.0f, 37, 37, 1, 0.1f, 1.0f };
		u32 h = particles::create_emitter(d, w);
		particles::play(h);
		particles::update(w, DT);
		check("burst at t=0 spawns its count on the first frame", particles::alive_count(h) == 37, std::to_string(particles::alive_count(h)));
		step(w, 60);
		check("a single-cycle burst fires once", particles::alive_count(h) == 37);

		d.bursts[0] = particles::Burst{ 0.1f, 10, 10, 3, 0.2f, 1.0f };   // at 0.1, 0.3, 0.5
		d.duration = 5.0f;
		u32 cyc = particles::create_emitter(d, w);
		particles::play(cyc);
		step(w, 3);    // 0.05 s
		u32 a = particles::alive_count(cyc);
		step(w, 6);    // 0.15 s
		u32 b = particles::alive_count(cyc);
		step(w, 30);   // 0.65 s
		u32 c = particles::alive_count(cyc);
		check("burst time respected (nothing before 0.1 s)", a == 0, std::to_string(a));
		check("burst cycles: 1st at 0.1 s", b == 10, std::to_string(b));
		check("burst cycles: 3 x 10 by 0.65 s", c == 30, std::to_string(c));

		d.bursts[0] = particles::Burst{ 0.0f, 10, 20, 1, 0.1f, 1.0f };
		bool in_range = true;
		for (int i = 0; i < 10; ++i)
		{
			d.seed = 100 + (u32)i;
			u32 r = particles::create_emitter(d, w);
			particles::play(r);
			particles::update(w, DT);
			u32 n = particles::alive_count(r);
			if (n < 10 || n > 20) in_range = false;
		}
		check("burst count range [10, 20]", in_range);

		// looping bursts repeat every cycle
		d = base_desc(); d.rate = 0.0f; d.looping = 1; d.duration = 0.25f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		d.burst_count = 1; d.bursts[0] = particles::Burst{ 0.0f, 5, 5, 1, 0.1f, 1.0f };
		u32 lb = particles::create_emitter(d, w);
		particles::play(lb);
		step(w, 59);   // 0.983 s = 4 cycles (0, 0.25, 0.5, 0.75); the 5th starts at 1.0
		check("a looping burst fires once per cycle", particles::alive_count(lb) == 20, std::to_string(particles::alive_count(lb)));

		// manual burst works while stopped
		d = base_desc(); d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		u32 m = particles::create_emitter(d, w);
		particles::burst(m, 25);
		check("burst() spawns immediately, even when not playing", particles::alive_count(m) == 25);
		particles::clear(w);
	}

	void run_motion_tests()
	{
		std::cout << "\n-- motion --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.rate = 0.0f; d.gravity = 1.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		u32 h = particles::create_emitter(d, w);
		particles::burst(h, 1);
		step(w, 60);
		float p[3] = {};
		particles::get_particle_positions(h, p, 1);
		// semi-implicit Euler: y = -g dt^2 n(n+1)/2 = -4.987 after 60 steps of 1/60
		check("gravity: free fall 1 s ~ -4.9 m", p[1] < -4.8f && p[1] > -5.1f, "y " + num(p[1]));

		d.gravity = 0.0f; d.speed[0] = d.speed[1] = 10.0f; d.drag = 2.0f;
		d.shape = particles::SHAPE_POINT;   // emits along +Z
		u32 dr = particles::create_emitter(d, w);
		particles::burst(dr, 1);
		step(w, 60);
		particles::get_particle_positions(dr, p, 1);
		// v(t) = 10 e^{-2t} -> distance ~ 10/2 (1 - e^{-2}) = 4.32
		check("drag: exponential slow-down (~4.3 m in 1 s along +Z)", p[2] > 4.0f && p[2] < 4.6f && std::fabs(p[0]) < 1e-4f, "z " + num(p[2]));

		// velocity over lifetime + speed curve
		d = base_desc(); d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		d.velocity[0] = 2.0f;
		u32 vol = particles::create_emitter(d, w);
		particles::burst(vol, 1);
		step(w, 30);
		particles::get_particle_positions(vol, p, 1);
		check("velocity over lifetime: 2 m/s for 0.5 s", std::fabs(p[0] - 1.0f) < 0.02f, "x " + num(p[0]));

		// local vs world simulation space: moving the emitter drags local particles along
		d = base_desc(); d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		d.simulation_space = particles::SPACE_LOCAL;
		u32 loc = particles::create_emitter(d, w);
		d.simulation_space = particles::SPACE_WORLD;
		u32 wor = particles::create_emitter(d, w);
		float m[16] = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
		particles::set_transform(loc, m); particles::set_transform(wor, m);
		particles::burst(loc, 1); particles::burst(wor, 1);
		m[12] = 5.0f;
		particles::set_transform(loc, m); particles::set_transform(wor, m);
		particles::update(w, DT);
		float pl[3], pw[3];
		particles::get_particle_positions(loc, pl, 1);
		particles::get_particle_positions(wor, pw, 1);
		check("local space: particles follow the emitter", std::fabs(pl[0] - 5.0f) < 1e-4f, "x " + num(pl[0]));
		check("world space: particles stay where they were emitted", std::fabs(pw[0]) < 1e-4f, "x " + num(pw[0]));

		// emitter rotation: +Z of a yaw 90 deg emitter points along +X (engine convention)
		d = base_desc(); d.rate = 0.0f; d.speed[0] = d.speed[1] = 1.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		u32 rot = particles::create_emitter(d, w);
		float ry[16] = { 0, 0, -1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1 };   // Ry(+90): local Z -> world +X
		particles::set_transform(rot, ry);
		particles::burst(rot, 1);
		step(w, 60);
		particles::get_particle_positions(rot, p, 1);
		check("shape direction follows the emitter rotation", p[0] > 0.95f && std::fabs(p[2]) < 0.05f, "p " + num(p[0]) + "," + num(p[2]));
		particles::clear(w);
	}

	void run_shape_tests()
	{
		std::cout << "\n-- shapes --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 10.0f; d.max_particles = 2000;
		std::vector<float> pos(2000 * 3);

		d.shape = particles::SHAPE_SPHERE; d.radius = 2.0f; d.radius_thickness = 0.0f;
		u32 s = particles::create_emitter(d, w);
		particles::burst(s, 500);
		u32 n = particles::get_particle_positions(s, pos.data(), 2000);
		bool on_surface = n == 500;
		for (u32 i = 0; i < n; ++i) if (std::fabs(std::sqrt(pos[i * 3] * pos[i * 3] + pos[i * 3 + 1] * pos[i * 3 + 1] + pos[i * 3 + 2] * pos[i * 3 + 2]) - 2.0f) > 1e-3f) on_surface = false;
		check("sphere shell: every particle on the radius", on_surface);

		d.radius_thickness = 1.0f;
		u32 v = particles::create_emitter(d, w);
		particles::burst(v, 1000);
		n = particles::get_particle_positions(v, pos.data(), 2000);
		u32 inner = 0; bool inside = true;
		for (u32 i = 0; i < n; ++i)
		{
			float r = std::sqrt(pos[i * 3] * pos[i * 3] + pos[i * 3 + 1] * pos[i * 3 + 1] + pos[i * 3 + 2] * pos[i * 3 + 2]);
			if (r > 2.0f + 1e-3f) inside = false;
			if (r < 2.0f * 0.7937f) ++inner;   // half the volume lies inside r * cbrt(0.5)
		}
		check("sphere volume: inside, uniform by volume", inside && inner > 400 && inner < 600, std::to_string(inner) + "/1000 inner");

		d.shape = particles::SHAPE_HEMISPHERE; d.radius_thickness = 0.0f;
		u32 hs = particles::create_emitter(d, w);
		particles::burst(hs, 300);
		n = particles::get_particle_positions(hs, pos.data(), 2000);
		bool front = true;
		for (u32 i = 0; i < n; ++i) if (pos[i * 3 + 2] < -1e-4f) front = false;
		check("hemisphere: only the +Z half", front);

		d.shape = particles::SHAPE_BOX; d.box[0] = 2.0f; d.box[1] = 4.0f; d.box[2] = 6.0f; d.emit_from = particles::EMIT_VOLUME;
		u32 bx = particles::create_emitter(d, w);
		particles::burst(bx, 500);
		n = particles::get_particle_positions(bx, pos.data(), 2000);
		bool in_box = true;
		for (u32 i = 0; i < n; ++i) if (std::fabs(pos[i * 3]) > 1.0f || std::fabs(pos[i * 3 + 1]) > 2.0f || std::fabs(pos[i * 3 + 2]) > 3.0f) in_box = false;
		check("box volume: inside the extents", in_box);

		// cone directions within the half angle (point cone, speed 1 -> positions after 1 s = directions)
		d = base_desc(); d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 10.0f; d.max_particles = 2000;
		d.shape = particles::SHAPE_CONE; d.radius = 0.0f; d.angle = 20.0f; d.speed[0] = d.speed[1] = 1.0f;
		u32 cn = particles::create_emitter(d, w);
		particles::burst(cn, 500);
		step(w, 60);
		n = particles::get_particle_positions(cn, pos.data(), 2000);
		float min_cos = 1.0f;
		for (u32 i = 0; i < n; ++i)
		{
			float l = std::sqrt(pos[i * 3] * pos[i * 3] + pos[i * 3 + 1] * pos[i * 3 + 1] + pos[i * 3 + 2] * pos[i * 3 + 2]);
			if (l > 1e-6f) min_cos = std::min(min_cos, pos[i * 3 + 2] / l);
		}
		check("cone: every direction within the 20 deg half angle", min_cos >= std::cos(20.0f * 3.14159265f / 180.0f) - 1e-4f, "min cos " + num(min_cos));

		// circle: in the XY plane at the radius (thickness 0)
		d.shape = particles::SHAPE_CIRCLE; d.radius = 1.5f; d.radius_thickness = 0.0f; d.speed[0] = d.speed[1] = 0.0f;
		u32 ci = particles::create_emitter(d, w);
		particles::burst(ci, 200);
		n = particles::get_particle_positions(ci, pos.data(), 2000);
		bool ring = true;
		for (u32 i = 0; i < n; ++i) if (std::fabs(pos[i * 3 + 2]) > 1e-4f || std::fabs(std::sqrt(pos[i * 3] * pos[i * 3] + pos[i * 3 + 1] * pos[i * 3 + 1]) - 1.5f) > 1e-3f) ring = false;
		check("circle rim: z = 0, r = radius", ring);

		// edge along X
		d.shape = particles::SHAPE_EDGE; d.length = 3.0f;
		u32 ed = particles::create_emitter(d, w);
		particles::burst(ed, 200);
		n = particles::get_particle_positions(ed, pos.data(), 2000);
		bool line = true;
		for (u32 i = 0; i < n; ++i) if (std::fabs(pos[i * 3]) > 1.5f || std::fabs(pos[i * 3 + 1]) > 1e-5f || std::fabs(pos[i * 3 + 2]) > 1e-5f) line = false;
		check("edge: on the X segment", line);

		// shape offset + rotation
		d.shape = particles::SHAPE_POINT; d.shape_offset[0] = 0.0f; d.shape_offset[1] = 3.0f; d.shape_offset[2] = 0.0f;
		d.shape_rotation[0] = -90.0f;   // +Z -> +Y
		d.speed[0] = d.speed[1] = 1.0f;
		u32 off = particles::create_emitter(d, w);
		particles::burst(off, 1);
		step(w, 60);
		float p[3];
		particles::get_particle_positions(off, p, 1);
		check("shape offset + rotation (-90 X turns +Z up)", std::fabs(p[1] - 4.0f) < 0.02f && std::fabs(p[2]) < 0.02f, "y " + num(p[1]));
		particles::clear(w);
	}

	// Runs a noisy, gravity + drag + collision-free emitter and returns every position after `frames`.
	static std::vector<float> simulate(u32 seed, int frames, u32 threads)
	{
		particles::set_worker_threads(threads);
		u32 w = particles::create_world();
		auto d = base_desc();
		d.seed = seed; d.max_particles = 30000; d.rate = 20000.0f; d.lifetime[0] = 0.5f; d.lifetime[1] = 1.5f;
		d.shape = particles::SHAPE_SPHERE; d.radius = 0.5f; d.speed[0] = 1.0f; d.speed[1] = 4.0f;
		d.gravity = 0.3f; d.drag = 0.5f; d.noise_strength = 1.5f; d.noise_frequency = 0.7f; d.noise_octaves = 2;
		d.size_curve = particles::curve_linear(1.0f, 0.2f);
		d.color_gradient = particles::gradient_fade(1, 0.5f, 0.2f, 1, 0);
		d.burst_count = 1; d.bursts[0] = particles::Burst{ 0.1f, 50, 150, 3, 0.2f, 0.7f };
		u32 h = particles::create_emitter(d, w);
		float m[16] = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
		particles::set_transform(h, m);
		particles::play(h);
		for (int i = 0; i < frames; ++i)
		{
			m[12] = std::sin(i * 0.1f) * 2.0f;   // moving emitter: pose interpolation + inherited velocity paths
			particles::set_transform(h, m);
			particles::update(w, DT);
		}
		std::vector<float> out(particles::alive_count(h) * 3);
		particles::get_particle_positions(h, out.data(), (u32)(out.size() / 3));
		particles::clear(w);
		return out;
	}

	void run_determinism_tests()
	{
		std::cout << "\n-- determinism --\n";
		const u32 hw_threads = std::max(2u, particles::worker_threads());
		auto a = simulate(77, 90, 0);
		auto b = simulate(77, 90, 0);
		auto c = simulate(77, 90, hw_threads);
		auto d = simulate(78, 90, hw_threads);
		check("same seed -> same particle count", a.size() == b.size() && !a.empty(), std::to_string(a.size() / 3) + " particles");
		check("same seed -> bit-identical positions", a.size() == b.size() && std::memcmp(a.data(), b.data(), a.size() * 4) == 0);
		check("single-threaded == multi-threaded (" + std::to_string(hw_threads) + " workers)",
			a.size() == c.size() && std::memcmp(a.data(), c.data(), a.size() * 4) == 0);
		check("a different seed -> a different result", a.size() != d.size() || std::memcmp(a.data(), d.data(), a.size() * 4) != 0);
		particles::set_worker_threads(hw_threads);

		// restart() replays the same seeded sequence
		u32 w = particles::create_world();
		auto de = base_desc(); de.rate = 50.0f; de.speed[0] = 0.5f; de.speed[1] = 3.0f; de.shape = particles::SHAPE_SPHERE; de.lifetime[0] = de.lifetime[1] = 5.0f;
		u32 h = particles::create_emitter(de, w);
		particles::play(h);
		step(w, 30);
		std::vector<float> first(particles::alive_count(h) * 3);
		particles::get_particle_positions(h, first.data(), (u32)first.size() / 3);
		particles::restart(h);
		step(w, 30);
		std::vector<float> second(particles::alive_count(h) * 3);
		particles::get_particle_positions(h, second.data(), (u32)second.size() / 3);
		check("restart() replays the seeded sequence", first.size() == second.size() && !first.empty() && std::memcmp(first.data(), second.data(), first.size() * 4) == 0);
		particles::clear(w);
	}

	void check(const std::string& name, bool ok, const std::string& detail = std::string()) { check(name.c_str(), ok, detail); }

	void run_json_tests()
	{
		std::cout << "\n-- json --\n";
		const char* emitter = R"({
			"name": "Sparks", "maxParticles": 400, "duration": 0.3, "looping": false,
			"simulationSpace": "local", "rate": 0, "bursts": [ { "time": 0, "count": 30, "countMax": 40 } ],
			"lifetime": [0.2, 0.5], "speed": [4, 9], "size": 0.03, "rotation": [0, 360],
			"color": [1, 0.8, 0.4, 1], "color2": [1, 0.5, 0.1, 1], "gravity": 0.6, "drag": 1.5,
			"shape": { "type": "cone", "radius": 0.01, "angle": 18, "rotation": [0, 0, 0] },
			"sizeCurve": { "keys": [ { "t": 0, "v": 1 }, { "t": 1, "v": 0.2 } ] },
			"colorGradient": { "keys": [ { "t": 0, "r": 1, "g": 1, "b": 1, "a": 1 }, { "t": 1, "r": 1, "g": 0.3, "b": 0, "a": 0 } ] },
			"noise": { "strength": 0.4, "frequency": 2, "octaves": 2 },
			"flipbook": { "tilesX": 4, "tilesY": 2, "mode": "lifetime", "blend": true },
			"render": { "mode": "stretched", "blend": "additive", "softDistance": 0.1, "lengthScale": 3, "velocityScale": 0.02, "lit": false, "emissive": 2 },
			"collision": { "enabled": true, "mode": "bounce", "bounce": 0.3 },
			"trails": { "enabled": true, "lifetime": 0.2, "maxPoints": 12 },
			"someFutureKey": { "ignored": [1, 2, 3] } // comments are tolerated
		})";
		particles::EmitterJson ej;
		std::string err;
		bool ok = particles::parse_emitter_json(emitter, ej, &err);
		check("emitter JSON parses", ok, err);
		const auto& d = ej.desc;
		check("JSON: scalars / enums / ranges", d.max_particles == 400 && d.looping == 0 && d.simulation_space == particles::SPACE_LOCAL
			&& d.lifetime[0] == 0.2f && d.lifetime[1] == 0.5f && d.size[0] == 0.03f && d.size[1] == 0.03f
			&& d.render_mode == particles::RENDER_STRETCHED && d.blend == particles::BLEND_ADDITIVE && d.shape == particles::SHAPE_CONE
			&& d.angle == 18.0f && d.tiles_x == 4 && d.tiles_y == 2 && d.frame_blend == 1 && d.collision == 1 && d.trails == 1 && d.trail_max_points == 12);
		check("JSON: bursts", d.burst_count == 1 && d.bursts[0].count_min == 30 && d.bursts[0].count_max == 40);
		check("JSON: curves + gradients", d.size_curve.count == 2 && d.size_curve.v[1] == 0.2f && d.color_gradient.count == 2
			&& std::fabs(d.color_gradient.rgba[1][1] - 0.3f) < 1e-6f && d.color_gradient.rgba[1][3] == 0.0f);
		check("JSON: name", ej.name == "Sparks");

		u32 w = particles::create_world();
		u32 h = particles::create_emitter_json(emitter, w, &err);
		check("create_emitter_json", h != 0, err);
		particles::play(h);
		particles::update(w, DT);
		u32 n = particles::alive_count(h);
		check("JSON emitter bursts 30..40 on play", n >= 30 && n <= 40, std::to_string(n));
		check("set_emitter_json keeps the particles", particles::set_emitter_json(h, R"({"rate": 5, "bursts": []})") && particles::alive_count(h) == n);

		const char* effect = R"({ "name": "Muzzle", "emitters": [
			{ "name": "a", "rate": 10 }, { "name": "b", "enabled": false }, { "name": "c", "rate": 20 } ],
			"beam": { "width": 0.02, "speed": 300 } })";
		std::vector<particles::EmitterJson> list;
		particles::BeamJson beam;
		bool has_beam = false;
		ok = particles::parse_effect_json(effect, list, &beam, &has_beam, &err);
		check("effect JSON: 3 emitters (1 disabled) + beam", ok && list.size() == 3 && !list[1].enabled && has_beam && beam.desc.width == 0.02f && beam.desc.speed == 300.0f, err);

		bool bad1 = !particles::parse_emitter_json("{ \"rate\": ", ej, &err);
		bool bad2 = !particles::parse_emitter_json("[1, 2]", ej, &err);
		std::string deep(200, '[');
		bool bad3 = !particles::parse_emitter_json(deep.c_str(), ej, &err);
		check("malformed JSON is rejected (truncated, not an object, nested too deep)", bad1 && bad2 && bad3);
		check("create_emitter_json fails cleanly on garbage", particles::create_emitter_json("{{{", w, &err) == 0 && !err.empty());
		particles::clear(w);
	}

	static particles::ViewInfo look_at(float ex, float ey, float ez, float tx, float ty, float tz)
	{
		using namespace DirectX;
		particles::ViewInfo v{};
		XMVECTOR eye = XMVectorSet(ex, ey, ez, 1), at = XMVectorSet(tx, ty, tz, 1), up = XMVectorSet(0, 1, 0, 0);
		XMMATRIX vp = XMMatrixLookAtLH(eye, at, up) * XMMatrixPerspectiveFovLH(XMConvertToRadians(60.0f), 16.0f / 9.0f, 0.1f, 1000.0f);
		XMFLOAT4X4 m; XMStoreFloat4x4(&m, vp);
		std::memcpy(v.view_proj, &m, 64);
		XMVECTOR f = XMVector3Normalize(at - eye), r = XMVector3Normalize(XMVector3Cross(up, f)), u = XMVector3Cross(f, r);
		v.eye[0] = ex; v.eye[1] = ey; v.eye[2] = ez;
		XMFLOAT3 t;
		XMStoreFloat3(&t, f); v.forward[0] = t.x; v.forward[1] = t.y; v.forward[2] = t.z;
		XMStoreFloat3(&t, r); v.right[0] = t.x; v.right[1] = t.y; v.right[2] = t.z;
		XMStoreFloat3(&t, u); v.up[0] = t.x; v.up[1] = t.y; v.up[2] = t.z;
		v.ortho = false;
		return v;
	}

	void run_gather_tests()
	{
		std::cout << "\n-- gather (render side) --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 10.0f; d.shape = particles::SHAPE_SPHERE; d.radius = 3.0f;
		d.blend = particles::BLEND_ALPHA;
		u32 h = particles::create_emitter(d, w);
		particles::burst(h, 500);
		d.blend = particles::BLEND_ADDITIVE;
		u32 add = particles::create_emitter(d, w);
		particles::burst(add, 300);
		particles::update(w, 0.0f);
		particles::DrawList list;
		auto view = look_at(0, 2, -15, 0, 0, 0);
		bool any = particles::gather(w, view, list);
		check("gather: visible emitters produce batches", any && list.batches.size() == 2);
		check("gather: every alive particle becomes an instance", list.instances.size() == 800, std::to_string(list.instances.size()));
		bool sorted = true;
		const particles::DrawBatch* alpha = nullptr;
		for (auto& b : list.batches) if (b.blend == particles::BLEND_ALPHA) alpha = &b;
		if (alpha)
		{
			float prev = 1e30f;
			for (u32 i = 0; i < alpha->count; ++i)
			{
				const auto& p = list.instances[alpha->first + i];
				float dd = (p.pos[0] - view.eye[0]) * view.forward[0] + (p.pos[1] - view.eye[1]) * view.forward[1] + (p.pos[2] - view.eye[2]) * view.forward[2];
				if (dd > prev + 1e-4f) sorted = false;
				prev = dd;
			}
		}
		check("gather: alpha batch sorted back-to-front", alpha && sorted);
		auto away = look_at(0, 2, -15, 0, 2, -30);   // looking away from the emitters
		check("gather: emitters behind the camera are culled", !particles::gather(w, away, list) && list.instances.empty());

		// bigger sort (radix path) stays ordered
		d.blend = particles::BLEND_ALPHA; d.max_particles = 20000;
		u32 big = particles::create_emitter(d, w);
		particles::burst(big, 20000);
		particles::update(w, 0.0f);
		particles::gather(w, view, list);
		sorted = true; bool found = false;
		for (auto& b : list.batches)
		{
			if (b.count != 20000) continue;
			found = true;
			float prev = 1e30f;
			for (u32 i = 0; i < b.count; ++i)
			{
				const auto& p = list.instances[b.first + i];
				float dd = (p.pos[0] - view.eye[0]) * view.forward[0] + (p.pos[1] - view.eye[1]) * view.forward[1] + (p.pos[2] - view.eye[2]) * view.forward[2];
				if (dd > prev + 1e-4f) sorted = false;
				prev = dd;
			}
		}
		check("gather: 20k alpha particles radix-sorted back-to-front", found && sorted);

		// instance data: size curve + gradient + flipbook frame
		particles::clear(w);
		d = base_desc(); d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 1.0f; d.size[0] = d.size[1] = 2.0f;
		d.size_curve = particles::curve_linear(1.0f, 0.0f);
		d.color_gradient = particles::gradient_fade(1, 1, 1, 1, 0);
		d.tiles_x = 4; d.tiles_y = 4; d.flipbook_mode = particles::FLIPBOOK_LIFETIME;
		u32 c = particles::create_emitter(d, w);
		particles::burst(c, 1);
		step(w, 30);   // t = 0.5
		particles::gather(w, view, list);
		bool inst_ok = list.instances.size() == 1;
		if (inst_ok)
		{
			const auto& p = list.instances[0];
			inst_ok = std::fabs(p.size - 1.0f) < 0.02f && std::fabs((float)(p.color >> 24) - 127.5f) < 3.0f && std::fabs(p.frame - 8.0f) < 0.3f;
			if (!inst_ok) std::cout << "   size " << p.size << " alpha " << (p.color >> 24) << " frame " << p.frame << "\n";
		}
		check("instance: size curve, alpha gradient, flipbook frame at half life", inst_ok);
		particles::clear(w);
	}

	void run_trail_and_beam_tests()
	{
		std::cout << "\n-- trails & beams --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.rate = 0.0f; d.lifetime[0] = d.lifetime[1] = 5.0f; d.speed[0] = d.speed[1] = 5.0f;
		d.trails = 1; d.trail_lifetime = 0.5f; d.trail_min_distance = 0.05f; d.trail_max_points = 16;
		u32 h = particles::create_emitter(d, w);
		particles::burst(h, 10);
		step(w, 20);
		particles::DrawList list;
		auto view = look_at(10, 2, 0, 0, 0, 1);
		particles::gather(w, view, list);
		bool trail_batch = false;
		for (auto& b : list.batches) if (b.kind == 1) trail_batch = true;
		check("trails: ribbon batch with vertices", trail_batch && list.ribbon_vertices.size() >= 10 * 4 && list.ribbon_indices.size() % 6 == 0,
			std::to_string(list.ribbon_vertices.size()) + " verts");
		bool width_ok = true;
		for (size_t i = 0; i + 1 < list.ribbon_vertices.size(); i += 2)
		{
			const auto& a = list.ribbon_vertices[i]; const auto& b = list.ribbon_vertices[i + 1];
			float dx = a.pos[0] - b.pos[0], dy = a.pos[1] - b.pos[1], dz = a.pos[2] - b.pos[2];
			if (std::sqrt(dx * dx + dy * dy + dz * dz) > 0.25f + 1e-3f) width_ok = false;   // size 0.25 x width 1
		}
		check("trails: strip width = particle size x trail width", width_ok);

		particles::BeamDesc bd;
		particles::default_beam_desc(bd);
		bd.width = 0.1f; bd.segments = 8; bd.duration = 0.5f; bd.noise = 0.05f;
		u32 b = particles::create_beam(bd, w);
		float from[3] = { 0, 1, 0 }, to[3] = { 0, 1, 20 };
		particles::set_beam_points(b, from, to);
		particles::clear_particles(h);
		particles::gather(w, view, list);
		check("beam: one strip of segments + 1 point pairs", list.ribbon_vertices.size() == 18 && list.ribbon_indices.size() == 48, std::to_string(list.ribbon_vertices.size()));
		step(w, 31);
		check("beam: destroyed after its duration", !particles::beam_valid(b) && particles::beam_count(w) == 0);

		std::string err;
		u32 tracer = particles::create_beam_json(R"({"beam": {"width": 0.03, "speed": 100, "length": 3, "duration": 0.3}})", w, &err);
		particles::set_beam_points(tracer, from, to);
		particles::update(w, 0.05f);   // head at 5 m, tail at 2 m
		particles::gather(w, view, list);
		float zmin = 1e9f, zmax = -1e9f;
		for (auto& v : list.ribbon_vertices) { zmin = std::min(zmin, v.pos[2]); zmax = std::max(zmax, v.pos[2]); }
		check("tracer: a 3 m streak travelling at 100 m/s (2..5 m after 0.05 s)", tracer != 0 && std::fabs(zmin - 2.0f) < 0.05f && std::fabs(zmax - 5.0f) < 0.05f,
			num(zmin) + ".." + num(zmax));
		particles::clear(w);
	}

	void run_collision_tests()
	{
		std::cout << "\n-- depth collision --\n";
		// A camera 10 m above a floor at y = 0, looking straight down: the floor's view depth is 10 everywhere.
		const u32 W = 64, H = 64;
		std::vector<float> depth(W * H, 10.0f);
		particles::DepthView v{};
		v.eye[0] = 0; v.eye[1] = 10; v.eye[2] = 0;
		v.forward[0] = 0; v.forward[1] = -1; v.forward[2] = 0;
		v.right[0] = 1; v.right[1] = 0; v.right[2] = 0;
		v.up[0] = 0; v.up[1] = 0; v.up[2] = 1;
		v.tan_half_x = v.tan_half_y = 1.0f;
		v.near_clip = 0.1f; v.far_clip = 1000.0f;
		particles::submit_depth_snapshot(depth.data(), W, H, v);

		auto d = base_desc();
		d.rate = 0.0f; d.gravity = 1.0f; d.lifetime[0] = d.lifetime[1] = 10.0f;
		d.collision = 1; d.collision_mode = particles::COLLIDE_BOUNCE; d.bounce = 0.5f; d.dampen = 0.0f; d.collision_radius = 0.02f;
		u32 b = particles::create_emitter(d, 0);   // collision is a scene (world 0) feature
		float m[16] = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 2, 0, 1 };
		particles::set_transform(b, m);
		particles::burst(b, 1);
		check("wants_depth_snapshot with a colliding emitter", particles::wants_depth_snapshot());
		float lowest = 1e9f, p[3] = {};
		bool rose = false; float prev = 2.0f;
		for (int i = 0; i < 120; ++i)
		{
			particles::update(0, DT);
			particles::get_particle_positions(b, p, 1);
			lowest = std::min(lowest, p[1]);
			if (p[1] > prev + 1e-4f) rose = true;
			prev = p[1];
		}
		check("bounce: never sinks through the floor", lowest > -0.05f, "lowest y " + num(lowest));
		check("bounce: moves back up after the hit", rose);

		d.collision_mode = particles::COLLIDE_KILL;
		u32 k = particles::create_emitter(d, 0);
		particles::set_transform(k, m);
		particles::burst(k, 20);
		step(0, 60);
		check("kill mode: particles die on contact", particles::alive_count(k) == 0, std::to_string(particles::alive_count(k)));
		particles::clear(0);
		check("clear(0) drops the depth snapshot users", !particles::wants_depth_snapshot());
	}

	static int s_callbacks;
	static float s_last_dt;
	static void frame_cb(float dt) { ++s_callbacks; s_last_dt = dt; }

	void run_handle_and_callback_tests()
	{
		std::cout << "\n-- handles, callback, stats --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		u32 a = particles::create_emitter(d, w);
		particles::destroy_emitter(a);
		u32 b = particles::create_emitter(d, w);   // reuses the slot with a new generation
		check("a destroyed handle is stale, the reused slot gets a new handle", !particles::emitter_valid(a) && particles::emitter_valid(b) && a != b);
		particles::play(a); particles::burst(a, 10); particles::set_transform(a, nullptr);
		check("calls on stale handles are no-ops", particles::alive_count(a) == 0 && particles::alive_count(b) == 0);
		check("handle 0 is invalid", !particles::emitter_valid(0) && !particles::beam_valid(0));
		particles::BeamDesc bd; particles::default_beam_desc(bd);
		u32 beam = particles::create_beam(bd, w);
		check("beam and emitter handle spaces are distinct", !particles::emitter_valid(beam) && !particles::beam_valid(b));

		particles::set_frame_callback(&frame_cb);
		particles::begin_frame();
		particles::begin_frame();
		check("frame callback invoked once per begin_frame", s_callbacks == 2 && s_last_dt >= 0.0f && s_last_dt <= 0.1f);
		particles::set_frame_callback(nullptr);
		d.rate = 1000.0f;
		u32 auto0 = particles::create_emitter(d, 0);
		particles::play(auto0);
		particles::begin_frame();
		check("without a callback begin_frame updates world 0", particles::alive_count(auto0) > 0 || particles::emitter_time(auto0) > 0.0f);
		particles::clear(0);

		particles::burst(b, 42);
		particles::Stats st{};
		particles::get_stats(w, st);
		check("stats: emitters / beams / alive per world", st.emitters == 1 && st.beams == 1 && st.alive_particles == 42);
		particles::clear(w);
	}

	void run_performance_test()
	{
		std::cout << "\n-- performance --\n";
		u32 w = particles::create_world();
		auto d = base_desc();
		d.max_particles = 150000; d.rate = 60000.0f; d.lifetime[0] = 1.5f; d.lifetime[1] = 2.0f;
		d.shape = particles::SHAPE_SPHERE; d.radius = 1.0f; d.speed[0] = 0.5f; d.speed[1] = 2.0f;
		d.gravity = 0.2f; d.drag = 0.3f;
		d.size_curve = particles::curve_linear(1.0f, 0.3f);
		d.color_gradient = particles::gradient_fade(1, 0.8f, 0.6f, 1, 0);
		u32 h = particles::create_emitter(d, w);
		particles::play(h);
		step(w, 150);   // warm up to the steady state (~105k)
		auto time_frames = [&](int frames) -> double
		{
			auto t0 = std::chrono::steady_clock::now();
			step(w, frames);
			return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / frames;
		};
		double plain = time_frames(60);
		u32 alive = particles::alive_count(h);
		const u32 workers = particles::worker_threads();
		particles::set_worker_threads(0);
		double single = time_frames(30);
		particles::set_worker_threads(workers);
		d.noise_strength = 1.0f; d.noise_octaves = 1;
		particles::set_emitter_desc(h, d);
		double noisy = time_frames(60);
		particles::DrawList list;
		auto view = look_at(0, 2, -12, 0, 0, 0);
		d.blend = particles::BLEND_ADDITIVE;   // additive: order-independent, no sort
		particles::set_emitter_desc(h, d);
		auto t0 = std::chrono::steady_clock::now();
		for (int i = 0; i < 20; ++i) particles::gather(w, view, list);
		double gather_add = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / 20;
		d.blend = particles::BLEND_ALPHA;
		particles::set_emitter_desc(h, d);
		t0 = std::chrono::steady_clock::now();
		for (int i = 0; i < 20; ++i) particles::gather(w, view, list);
		double gather_sorted = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / 20;
		std::cout << "   " << alive << " particles, " << workers << " workers: simulate " << num(plain) << " ms/frame (1 thread: " << num(single)
			<< " ms), with noise " << num(noisy) << " ms, gather " << num(gather_add) << " ms (additive) / " << num(gather_sorted) << " ms (alpha, sorted)\n";
		check("100k+ particles alive", alive >= 100000, std::to_string(alive));
		// generous bound: this runs on shared build machines; the numbers above are the measurement
		check("simulation of 100k+ particles fits a 60 fps frame (< 12 ms)", plain < 12.0 && noisy < 16.0, num(plain) + " / " + num(noisy) + " ms");
		particles::clear(w);
	}
};

int engine_test::s_callbacks = 0;
float engine_test::s_last_dt = 0.0f;
