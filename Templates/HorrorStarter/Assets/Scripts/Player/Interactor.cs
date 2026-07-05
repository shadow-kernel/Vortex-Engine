using Vortex;

// [E] interaction — attach to a CHILD of the Player. Raycasts where you look; anything tagged
// "Interactable" shows a prompt and receives SendMessage("interact") on E. Interactables just
// override OnMessage — see SlidingDoor.cs. This is the whole interaction system: one raycast,
// one message, no registry.
public class Interactor : VortexBehaviour
{
    public float Reach = 2.8f;

    private bool _eHeld;

    public override void Update(float dt)
    {
        RaycastHit hit;
        bool interactable = Physics.Raycast(Position, Forward, Reach, out hit)
                            && hit.Tag == "Interactable";

        if (interactable)
        {
            float W = UI.Width, H = UI.Height;
            if (W > 10f)
            {
                UI.Rect(W * 0.5f - 130f, H * 0.62f - 4f, 260f, 28f, Color.Rgba(10, 10, 14, 170), 6f);
                UI.Text("[E]  " + Scene.NameOf(hit.EntityId), W * 0.5f - 120f, H * 0.62f, 240f, 20f, 13f,
                    Color.Rgba(235, 235, 240, 235), 1, 600);
            }
        }

        bool e = Input.GetKey("E") || Input.GetGamepadButtonDown("X");
        if (e && !_eHeld && interactable) SendMessage(hit.EntityId, "interact", null);
        _eHeld = e;
    }
}
