using System.Collections;
using Vortex;

// A bunker-style sliding door. Tag the entity "Interactable" — the player's Interactor sends
// "interact" and the door slides up/down over SlideTime seconds (a coroutine, #37). When the
// slide finishes, Physics.RefreshCollider re-bakes the door's collider at the new position so
// the doorway actually opens up for the character and for raycasts.
public class SlidingDoor : VortexBehaviour
{
    public float SlideHeight = 2.4f;
    public float SlideTime   = 1.4f;

    private bool _open;
    private bool _busy;

    public override void OnMessage(string message, object arg)
    {
        if (message == "interact" && !_busy) StartCoroutine(Slide(!_open));
    }

    private IEnumerator Slide(bool open)
    {
        _busy = true;
        _open = open;
        Vector3 start = Position;
        float from = start.Y;
        float to = open ? from + SlideHeight : from - SlideHeight;
        float t = 0f;
        while (t < SlideTime)
        {
            t += Time.DeltaTime;
            float k = t / SlideTime; if (k > 1f) k = 1f;
            k = k * k * (3f - 2f * k);   // smoothstep — eases in and out
            Position = new Vector3(start.X, from + (to - from) * k, start.Z);
            yield return null;
        }
        Physics.RefreshCollider(EntityId);   // the collision world is static — re-bake at the new spot
        _busy = false;
    }
}
