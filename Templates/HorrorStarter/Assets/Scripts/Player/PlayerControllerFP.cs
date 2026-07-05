using Vortex;

// First-person horror movement — 100% game-side, adapt freely. WASD = move, mouse = look,
// Shift = sprint, Space = jump, Ctrl/C = crouch, ESC = pause (Q quits while paused).
// Movement is classic Quake/Source: ground friction + wish-direction acceleration = crisp,
// with limited air control. The capsule goes through the engine's collide-and-slide, so
// walls/props are solid and you never clip.
public class PlayerControllerFP : VortexBehaviour
{
    public float WalkSpeed   = 4.6f;   // slower than an action game — horror walks
    public float SprintSpeed = 7.5f;
    public float CrouchSpeed = 2.4f;
    public float MouseSens   = 0.10f;
    public float JumpSpeed   = 7.5f;
    public float Gravity     = 26f;
    public float CrouchDrop  = 0.7f;
    public float Fov         = 68f;
    public float GroundAccel = 12f;
    public float AirAccel    = 2.5f;
    public float Friction    = 9f;
    public float PadLookSpeed = 220f;

    private float _standEyeY;
    private float _vx, _vz, _vy;
    private bool  _grounded;
    private bool  _jumpHeld;
    private bool  _escHeld;
    private bool  _paused;
    private float _pitch, _yaw;

    public override void Start()
    {
        Cursor.Locked = true;
        _standEyeY = Position.Y;
        Vector3 r = Rotation; _pitch = r.X; _yaw = r.Y;
        _grounded = true;
        Camera.SetFieldOfView(Fov);
    }

    public override void Update(float dt)
    {
        if (dt <= 0f) return;

        bool esc = Input.GetKey("Escape") || Input.GetGamepadButtonDown("Start");
        if (esc && !_escHeld) { _paused = !_paused; Cursor.Locked = !_paused; }
        _escHeld = esc;

        if (_paused)
        {
            DrawPauseOverlay();
            if (Input.GetKey("Q")) Application.Quit();
            _vx = 0f; _vz = 0f;
            return;
        }

        if (Cursor.Locked) MovePlayer(dt);
        DrawHud();
    }

    private void MovePlayer(float dt)
    {
        // ---- look ----
        _yaw   += Input.MouseDeltaX * MouseSens;
        _pitch += Input.MouseDeltaY * MouseSens;
        _yaw   += Input.RightStickX * PadLookSpeed * dt;
        _pitch -= Input.RightStickY * PadLookSpeed * dt;
        if (float.IsNaN(_yaw)   || float.IsInfinity(_yaw))   _yaw = 0f;
        if (float.IsNaN(_pitch) || float.IsInfinity(_pitch)) _pitch = 0f;
        if (_pitch > 89f) _pitch = 89f; else if (_pitch < -89f) _pitch = -89f;
        _yaw %= 360f;
        Rotation = new Vector3(_pitch, _yaw, 0f);

        // ---- wish direction ----
        bool crouch = Input.GetKey("LeftCtrl") || Input.GetKey("C") || Input.GetGamepadButton("B");
        bool sprint = Input.GetKey("LeftShift") || Input.GetGamepadButton("LeftStick");
        float maxSpeed = crouch ? CrouchSpeed : (sprint ? SprintSpeed : WalkSpeed);

        double yawRad = _yaw * System.Math.PI / 180.0;
        float fX = (float)System.Math.Sin(yawRad), fZ = (float)System.Math.Cos(yawRad);
        float rX = (float)System.Math.Cos(yawRad), rZ = (float)-System.Math.Sin(yawRad);
        float dx = 0f, dz = 0f;
        if (Input.GetKey("W")) { dx += fX; dz += fZ; }
        if (Input.GetKey("S")) { dx -= fX; dz -= fZ; }
        if (Input.GetKey("D")) { dx += rX; dz += rZ; }
        if (Input.GetKey("A")) { dx -= rX; dz -= rZ; }
        dx += fX * Input.LeftStickY + rX * Input.LeftStickX;
        dz += fZ * Input.LeftStickY + rZ * Input.LeftStickX;
        float wl = (float)System.Math.Sqrt(dx * dx + dz * dz);
        float wishX = 0f, wishZ = 0f;
        if (wl > 0.001f) { wishX = dx / wl; wishZ = dz / wl; }

        // ---- Quake/Source: friction on ground, accelerate toward the wish direction ----
        if (_grounded) { ApplyFriction(dt); Accelerate(wishX, wishZ, maxSpeed, GroundAccel, dt); }
        else Accelerate(wishX, wishZ, maxSpeed, AirAccel, dt);
        if (float.IsNaN(_vx) || float.IsInfinity(_vx)) _vx = 0f;
        if (float.IsNaN(_vz) || float.IsInfinity(_vz)) _vz = 0f;
        if (float.IsNaN(_vy) || float.IsInfinity(_vy)) _vy = 0f;

        // ---- move through the engine collision (capsule collide-and-slide) ----
        bool jump = Input.GetKey("Space") || Input.GetGamepadButton("A");
        if (_grounded && jump && !_jumpHeld) { _vy = JumpSpeed; _grounded = false; }
        else if (_grounded && _vy < 0f) _vy = 0f;
        _vy -= Gravity * dt;
        _jumpHeld = jump;

        float eyeOffset = crouch ? _standEyeY - CrouchDrop : _standEyeY;
        Vector3 cam = Position;
        Vector3 feet = new Vector3(cam.X, cam.Y - eyeOffset, cam.Z);
        Vector3 disp = new Vector3(_vx * dt, _vy * dt, _vz * dt);
        feet = Physics.MoveCharacter(feet, 0.35f, 1.85f, disp, EntityId);
        _grounded = Physics.Grounded;
        if (_grounded && _vy < 0f) _vy = 0f;
        Position = new Vector3(feet.X, feet.Y + eyeOffset, feet.Z);
    }

    private void Accelerate(float wishX, float wishZ, float wishSpeed, float accel, float dt)
    {
        float current = _vx * wishX + _vz * wishZ;
        float add = wishSpeed - current;
        if (add <= 0f) return;
        float accelSpeed = accel * wishSpeed * dt;
        if (accelSpeed > add) accelSpeed = add;
        _vx += wishX * accelSpeed;
        _vz += wishZ * accelSpeed;
    }

    private void ApplyFriction(float dt)
    {
        float speed = (float)System.Math.Sqrt(_vx * _vx + _vz * _vz);
        if (speed < 0.0001f) { _vx = 0f; _vz = 0f; return; }
        float newSpeed = speed - speed * Friction * dt;
        if (newSpeed < 0f) newSpeed = 0f;
        float scale = newSpeed / speed;
        _vx *= scale; _vz *= scale;
    }

    private void DrawHud()
    {
        float W = UI.Width, H = UI.Height;
        if (W < 10f) return;
        UI.Text(Settings.CurrentFps + " FPS", W - 138f, 12f, 126f, 26f, 16f, Color.Rgb(120, 230, 150), 2, 700);
        UI.Rect(W * 0.5f - 1f, H * 0.5f - 8f, 2f, 16f, Color.Rgba(255, 255, 255, 150), 0f);
        UI.Rect(W * 0.5f - 8f, H * 0.5f - 1f, 16f, 2f, Color.Rgba(255, 255, 255, 150), 0f);
    }

    private void DrawPauseOverlay()
    {
        float W = UI.Width, H = UI.Height;
        if (W < 10f) return;
        UI.Rect(0f, 0f, W, H, Color.Rgba(5, 5, 8, 170), 0f);
        UI.Text("PAUSED", 0f, H * 0.38f, W, 40f, 30f, Color.Rgba(235, 235, 240, 255), 1, 700);
        UI.Text("[ESC] resume        [Q] quit", 0f, H * 0.48f, W, 22f, 14f, Color.Rgba(170, 170, 180, 230), 1, 500);
    }
}
