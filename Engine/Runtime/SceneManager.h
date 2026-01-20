#pragma once

#include "../Common/CommonHeaders.h"
#include "../Common/Id.h"
#include "../Components/Entity.h"
#include "../Components/Transform.h"

namespace vortex::runtime::scene_manager {

    DEFINE_TYPED_ID(scene_id);

    scene_id create_scene();
    void destroy_scene(scene_id id);

    void activate_scene(scene_id id);
    void deactivate_scene(scene_id id);

    game_entity::entity create_entity(scene_id id, const transform::init_info& info);
    void remove_entity(scene_id id, game_entity::entity entity);

    bool is_scene_alive(scene_id id);
}
