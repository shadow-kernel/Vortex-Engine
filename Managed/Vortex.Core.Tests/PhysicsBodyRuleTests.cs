using Editor.Core.Services.Physics;
using Editor.ECS;
using Editor.ECS.Components.Physics;
using Editor.ECS.Components.Rendering;

namespace VortexTests
{
    /// <summary>When the physics world borrows the render mesh as a body (#341): only for a Rigidbody that has no
    /// Collider component at all — an entity whose colliders are all disabled gets no body, whatever re-adds it.</summary>
    public static class PhysicsBodyRuleTests
    {
        [Test]
        public static void OnlyAColliderlessRigidbodyBorrowsTheMesh(TestContext t)
        {
            var e = new GameEntity { Name = "Crate" };
            e.Components.Add(new Rigidbody(e));
            e.Components.Add(new MeshRenderer(e, "Primitive:Cube"));
            t.True(PhysicsService.UsesMeshAsBody(e), "a Rigidbody + mesh without a Collider uses the mesh");

            var col = new BoxCollider(e);
            e.Components.Add(col);
            t.False(PhysicsService.UsesMeshAsBody(e), "a collider is the body now");
            col.IsEnabled = false;
            t.False(PhysicsService.UsesMeshAsBody(e), "a DISABLED collider means no body, not the mesh (#341)");

            e.Components.Remove(col);
            e.GetComponent<Rigidbody>().IsEnabled = false;
            t.False(PhysicsService.UsesMeshAsBody(e), "no enabled Rigidbody, no body");
            t.False(PhysicsService.UsesMeshAsBody(null), "no entity, no body");
        }
    }
}
