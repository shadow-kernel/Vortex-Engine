using Vortex;

// Material-driven footsteps — attach to a CHILD of the Player. Every StepDistance meters of
// horizontal movement (while grounded) it asks the surface below which footstep sound its
// MATERIAL carries (assigned in the Material Editor) and plays it. Adding a new floor type
// never touches this script: assign the sound to the material, done.
public class FootstepAudio : VortexBehaviour
{
    public float StepDistance = 2.1f;
    public float SprintStepDistance = 2.7f;

    private Vector3 _last;
    private float _acc;

    public override void Start() { _last = Position; }

    public override void Update(float dt)
    {
        Vector3 p = Position;
        float dx = p.X - _last.X, dz = p.Z - _last.Z;
        _last = p;
        if (!Physics.Grounded) return;

        _acc += (float)System.Math.Sqrt(dx * dx + dz * dz);
        bool sprinting = Input.GetKey("LeftShift");
        float need = sprinting ? SprintStepDistance : StepDistance;
        if (_acc < need) return;
        _acc = 0f;

        string clip = Physics.GroundStepSound(p, 4f);
        if (clip != "") Audio.PlayOneShot(clip, p, sprinting ? 1f : 0.7f);
    }
}
