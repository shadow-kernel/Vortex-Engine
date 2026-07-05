using Vortex;

// The flashlight — attach to a SPOT-light entity that is a CHILD of the Player (the transform
// hierarchy makes it point where you look; no code needed for that). F toggles it, the battery
// drains while on and trickles back while off, low battery flickers like a dying bulb, and the
// spot light casts REAL shadows (Light component: Shadows = Soft).
public class FlashlightController : VortexBehaviour
{
    public float MaxBattery     = 100f;
    public float DrainPerSec    = 0.9f;    // ~110s of continuous light
    public float RechargePerSec = 0.35f;
    public float LowBatteryPct  = 22f;
    public float Intensity      = 40f;

    private bool  _on = true;
    private float _battery;
    private bool  _fHeld;
    private float _flickerT;

    public override void Start()
    {
        _battery = MaxBattery;
        _on = true;
        ApplyToLight();
    }

    public override void Update(float dt)
    {
        if (dt <= 0f) return;

        bool f = Input.GetKey("F") || Input.GetGamepadButtonDown("Y");
        if (f && !_fHeld) { _on = !_on; ApplyToLight(); }
        _fHeld = f;

        if (_on && _battery > 0f)
        {
            _battery -= DrainPerSec * dt;
            if (_battery <= 0f) { _battery = 0f; _on = false; ApplyToLight(); }
        }
        else if (!_on && _battery < MaxBattery)
        {
            _battery += RechargePerSec * dt;
            if (_battery > MaxBattery) _battery = MaxBattery;
        }

        _flickerT += dt;
        if (_on && _battery > 0f && _battery < LowBatteryPct) ApplyToLight();   // animate the dying bulb

        DrawBatteryHud();
    }

    private void ApplyToLight()
    {
        Light light = GetLight();
        if (light == null) return;
        if (!_on || _battery <= 0f) { light.Enabled = false; return; }
        light.Enabled = true;
        float intensity = Intensity;
        if (_battery < LowBatteryPct)
            intensity *= 0.5f + 0.5f * Flicker(_flickerT, 14f);
        light.Intensity = intensity;
    }

    private static float Flicker(float t, float speed)
    {
        float s = (float)System.Math.Sin(t * speed);
        float s2 = (float)System.Math.Sin(t * speed * 2.3f + 1.7f);
        float v = 0.5f + 0.35f * s + 0.15f * s2;
        return v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    private void DrawBatteryHud()
    {
        float W = UI.Width, H = UI.Height;
        if (W < 10f) return;
        float x = 24f, y = H - 44f, w = 150f, h = 20f;
        float pct = _battery / MaxBattery;
        Color fill = _battery <= 0f ? Color.Rgb(120, 30, 30)
                   : _battery < LowBatteryPct ? Color.Rgb(200, 140, 40)
                   : Color.Rgb(120, 200, 150);
        UI.Rect(x, y, w, h, Color.Rgba(15, 15, 18, 200), 5f);
        UI.Rect(x + 3f, y + 3f, (w - 6f) * (pct < 0f ? 0f : pct), h - 6f, fill, 3f);
        string label = _on ? (_battery <= 0f ? "DEAD" : "FLASHLIGHT [F]") : "OFF [F]";
        UI.Text(label, x, y - 20f, w + 40f, 16f, 12f, Color.Rgba(200, 200, 205, 220), 0, 600);
    }
}
