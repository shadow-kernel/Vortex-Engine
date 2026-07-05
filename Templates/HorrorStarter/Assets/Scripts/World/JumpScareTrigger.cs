using System.Collections;
using Vortex;

// The scripted jump scare — attach to an invisible entity with a Collider marked IsTrigger.
// When the player walks in (OnTriggerEnter, #34) it ramps the panic post-FX, spawns the
// stalker prefab (#36) and lets the scene's authored mood settle back afterwards. Fires once.
public class JumpScareTrigger : VortexBehaviour
{
    public float SpawnDistanceBehind = 3.5f;

    private bool _fired;

    public override void OnTriggerEnter(TriggerHit other)
    {
        if (_fired) return;
        _fired = true;
        StartCoroutine(Scare());
    }

    private IEnumerator Scare()
    {
        // dread builds: grain + color fringing surge
        PostFx.SetGrain(true, 0.55f, 1.8f);
        PostFx.SetChromaticAberration(true, 1.3f, 1.1f);
        yield return new WaitForSeconds(0.6f);

        // it appears BEHIND the player
        long player = Scene.Find("Player");
        Vector3 pos;
        float yaw = 0f;
        if (player != 0)
        {
            Vector3 pp = Scene.PositionOf(player);
            Vector3 fwd = Forward;   // trigger's own forward is fine as a fallback direction
            pos = new Vector3(pp.X - fwd.X * SpawnDistanceBehind, 0f, pp.Z - fwd.Z * SpawnDistanceBehind);
        }
        else pos = new Vector3(Position.X, 0f, Position.Z);
        Scene.Instantiate("Assets/Prefabs/Monster.ventity", pos, yaw);
        Debug.Log("Something is here.");

        // settle back to the scene's authored mood
        yield return new WaitForSeconds(2.5f);
        PostFx.SetChromaticAberration(false, 0f, 0f);
        PostFx.SetGrain(true, 0.25f, 1.6f);
    }
}
