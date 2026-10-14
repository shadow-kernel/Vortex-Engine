// Native game entities (VortexEntityTest): what the managed scene relies on since every loaded entity gets its own
// native entity — unique ids (also when removed slots are reused), each transform written to and read from its own
// entity, removing one entity (or a whole scene's worth, in any order) never touching another, and a native
// mesh-renderer slot handed out once even when it is removed twice. Headless; prints "ALL PASSED" or "FAILURES!".
#include "../Engine/Components/Entity.h"
#include "../Engine/Components/Transform.h"
#include "../Engine/Components/MeshRenderer.h"
#include "../Engine/Runtime/SceneManager.h"

#include <cmath>
#include <cstdio>
#include <unordered_set>
#include <vector>

using namespace vortex;
namespace sm = vortex::runtime::scene_manager;

static int g_failures = 0;
static void check(bool ok, const char* what)
{
	std::printf("  [%s] %s\n", ok ? "ok" : "FAIL", what);
	if (!ok) ++g_failures;
}

static transform::init_info at(f32 x, f32 y, f32 z)
{
	transform::init_info info{};
	info.position[0] = x; info.position[1] = y; info.position[2] = z;
	info.rotation[3] = 1.0f;
	return info;
}

static bool reads(game_entity::entity e, f32 x, f32 y, f32 z)
{
	if (!game_entity::is_alive(e)) return false;
	const math::v3 p{ e.transform().position() };
	return std::fabs(p.x - x) < 1e-5f && std::fabs(p.y - y) < 1e-5f && std::fabs(p.z - z) < 1e-5f;
}

static id::id_type raw(game_entity::entity e) { return (id::id_type)e.get_id(); }

int main()
{
	std::printf("Game entity test\n");

	// two entities of one scene: their own ids, their own transforms
	const sm::scene_id scene{ sm::create_scene() };
	sm::activate_scene(scene);
	const auto a = sm::create_entity(scene, at(1, 2, 3));
	const auto b = sm::create_entity(scene, at(4, 5, 6));
	check(a.is_valid() && b.is_valid() && raw(a) != raw(b), "two entities get two different valid ids");
	check(reads(a, 1, 2, 3) && reads(b, 4, 5, 6), "each reads back the transform it was created with");
	auto moved = at(7, 8, 9);
	transform::set_transform(a, moved);
	check(reads(a, 7, 8, 9) && reads(b, 4, 5, 6), "a transform update reaches its own entity only");

	// removing one leaves the other alone — also when the removal is repeated with the stale id
	sm::remove_entity(scene, a);
	check(!game_entity::is_alive(a) && reads(b, 4, 5, 6), "removing one entity keeps the other alive with its transform");
	sm::remove_entity(scene, a);
	check(reads(b, 4, 5, 6), "removing an already removed entity again touches nobody");

	// enough removals for the free list to hand indices out again (id::min_deleted_elements): reused indices come
	// with a new generation and each entity still reads its OWN transform slot
	std::vector<game_entity::entity> burst;
	for (u32 i = 0; i < id::min_deleted_elements + 100; ++i) burst.push_back(sm::create_entity(scene, at((f32)i, 0, 0)));
	for (auto e : burst) sm::remove_entity(scene, e);
	const auto c = sm::create_entity(scene, at(10, 11, 12));
	const auto d = sm::create_entity(scene, at(13, 14, 15));
	bool reused = false;
	for (auto e : burst) if (id::index(raw(e)) == id::index(raw(c)) || id::index(raw(e)) == id::index(raw(d))) reused = true;
	check(reused, "freed indices are reused once enough entities were removed");
	bool stale = true;
	for (auto e : burst) if (game_entity::is_alive(e)) stale = false;
	check(stale && raw(c) != raw(d) && game_entity::is_alive(c) && game_entity::is_alive(d), "a reused index is a new id: the old ones stay dead");
	check(reads(c, 10, 11, 12) && reads(d, 13, 14, 15) && reads(b, 4, 5, 6), "entities on reused indices read their own transforms");
	transform::set_transform(c, moved);
	check(reads(c, 7, 8, 9) && reads(d, 13, 14, 15), "and updates on reused indices reach only their own entity");

	// a whole scene deactivated entity by entity (creation order, as the managed side does), then the reverse order:
	// every entity goes, the other scene's do not, and the ids of the live ones never repeat
	const sm::scene_id other{ sm::create_scene() };
	const auto keep = sm::create_entity(other, at(-1, -2, -3));
	std::vector<game_entity::entity> many;
	for (u32 i = 0; i < 20000; ++i) many.push_back(sm::create_entity(scene, at((f32)i, 1, 0)));
	std::unordered_set<id::id_type> ids;
	for (auto e : many) ids.insert(raw(e));
	ids.insert(raw(b)); ids.insert(raw(c)); ids.insert(raw(d)); ids.insert(raw(keep));
	check(ids.size() == many.size() + 4, "20,000 more entities: every live id is unique");
	bool own = true;
	for (u32 i = 0; i < many.size() && own; i += 997) own = reads(many[i], (f32)i, 1, 0);
	check(own, "every one reads its own transform");
	for (u32 i = 0; i < many.size() / 2; ++i) sm::remove_entity(scene, many[i]);
	for (u32 i = (u32)many.size(); i-- > many.size() / 2;) sm::remove_entity(scene, many[i]);
	bool gone = true;
	for (auto e : many) if (game_entity::is_alive(e)) gone = false;
	check(gone, "removed in creation order and in reverse: all of them are gone");
	check(reads(b, 4, 5, 6) && reads(c, 7, 8, 9) && reads(d, 13, 14, 15) && reads(keep, -1, -2, -3), "the scene's other entities and the other scene's entity are untouched");
	sm::destroy_scene(scene);
	check(!game_entity::is_alive(b) && !game_entity::is_alive(c) && !game_entity::is_alive(d), "destroying the scene removes what it still had");
	check(reads(keep, -1, -2, -3), "the other scene keeps its entity");
	sm::remove_entity(other, keep);
	sm::destroy_scene(other);

	// native mesh-renderer components: a slot removed twice is handed out once, not to two renderers
	components::mesh_renderer_init_info info{};
	info.mesh_id = 5;
	const auto r1 = components::mesh_renderer::create(100, info);
	components::mesh_renderer::remove(r1);
	components::mesh_renderer::remove(r1);
	const auto r2 = components::mesh_renderer::create(101, info);
	const auto r3 = components::mesh_renderer::create(102, info);
	check(r2.value != r3.value, "a mesh renderer removed twice: the next two renderers still get two slots");
	check(components::mesh_renderer::get_entity(r2) == 101 && components::mesh_renderer::get_entity(r3) == 102, "each slot belongs to its own entity");
	components::mesh_renderer::remove(r2);
	components::mesh_renderer::remove(r3);

	std::printf(g_failures ? "FAILURES!\n" : "ALL PASSED\n");
	return g_failures ? 1 : 0;
}
