using Vortex;

// Scene mood bootstrap. Fog / vignette / grain are AUTHORED in the editor's Environment panel
// (saved in the scene) — this script only crushes the ambient light, which is script-driven.
// Tweak the value: 0 = pitch black between lights, 0.1 = you can just make out shapes.
public class HorrorAtmosphere : VortexBehaviour
{
    public float Ambient = 0.035f;

    public override void Start()
    {
        Lighting.SetAmbient(Ambient);
    }
}
