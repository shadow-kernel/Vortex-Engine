using Vortex;

// The spawned stalker (lives on the Monster prefab). Drifts toward the player, faces them,
// stops just short and looms — then despawns after LifeSeconds (Invoke + Scene.Destroy, #36/#37).
// No collider on purpose: it glides THROUGH geometry, which reads as a ghost and never gets stuck.
public class MonsterStalker : VortexBehaviour
{
    public float Speed = 2.4f;
    public float LoomDistance = 1.3f;
    public float LifeSeconds = 8f;

    private long _player;

    public override void Start()
    {
        _player = Scene.Find("Player");
        Invoke(Despawn, LifeSeconds);
    }

    private void Despawn() { Scene.Destroy(EntityId); }

    public override void Update(float dt)
    {
        if (_player == 0) return;
        Vector3 p = Scene.PositionOf(_player);
        Vector3 m = Position;
        float dx = p.X - m.X, dz = p.Z - m.Z;
        float d = (float)System.Math.Sqrt(dx * dx + dz * dz);

        float yaw = (float)(System.Math.Atan2(dx, dz) * 180.0 / System.Math.PI);
        Rotation = new Vector3(0f, yaw, 0f);

        if (d <= LoomDistance) return;   // close enough — just loom
        Position = new Vector3(m.X + dx / d * Speed * dt, m.Y, m.Z + dz / d * Speed * dt);
    }
}
