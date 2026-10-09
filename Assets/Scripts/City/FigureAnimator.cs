using UnityEngine;

// Procedural poses for a CharacterFigure (no imported animations). The owner sets the inputs each
// frame (velocity, grounded, look, pose, arms); LateUpdate turns them into joint rotations, blended so
// changes never snap.
//   Body yaw: with followLook (the player) the head turns first and the hips follow once the look is
//   more than turnThreshold degrees away or the character moves; otherwise the hips face the root.
//   Legs: idle sway, walk and run cycles with opposite arm swing, air pose, landing crouch.
//   Arms: Lowered (at the sides), Guard (fists up, aimed along the look), punches from the guard,
//   Behind (cuffed, straining while struggling), Aim (right arm left to PlayerWeapon's IK; the body
//   turns once the aim is more than 70 degrees off it, and leans for aim pitch beyond 60 degrees).
//   Whole-body poses: glide, hang, seated, stunned (lying), officer cuffing / dragging, scooter.
// Generated bodies (CharacterFigure.Generated) add a procedural layer from their DNA:
//   Size and fat: heavier bodies sway more and walk with their legs wider apart; STR lengthens the
//   stride (bigger swing, slower cadence).
//   Extra arms (beyond the first pair) follow the main arm on their side, smaller and a beat behind.
//   DEX jitter (from DEX 15 to 20): small fast noise on the head and wrists, faster idle weight
//   shifts and occasional quick glances. Head and wrists only: the player's camera rides the neck and
//   the aim sets the gun wrist afterwards, so neither the view nor the aim ever shakes.
// Rotation convention: limbs hang along -Y, so a negative X rotation swings them forward.
[DefaultExecutionOrder(50)]
public class FigureAnimator : MonoBehaviour
{
    public enum Pose { Normal, Air, Landing, Glide, Hang, Seated, Stunned, Fallen, Cuffed, Dragged, Cuffing, Dragging, Scooter, Staggered }
    public enum Arms { Lowered, Guard, Behind, Aim }

    // ---------- inputs ----------
    public Vector3 Velocity { get; set; }
    public bool Grounded { get; set; } = true;
    public Pose CurrentPose { get; set; }
    public Arms ArmMode { get; set; }
    public float LookYaw { get; set; }     // world yaw of the eyes
    public float LookPitch { get; set; }   // degrees, + = down
    public bool FollowLook { get; set; }
    public bool Straining { get; set; }    // mashing against restraints

    public float turnThreshold = 60f;
    public float blend = 12f;

    public CharacterFigure Figure { get; private set; }
    public float BodyYaw => bodyYaw;

    float bodyYaw, phase, idle;
    bool aimTurning;
    float glanceUntil = -1f, nextGlance, glanceYaw, noiseSeed;
    Quaternion[] extraShoulder, extraElbow;

    // Readouts for tests.
    public float Jitter { get; private set; }
    public float Heavy { get; private set; }
    float[] punchStart = { -10f, -10f };
    const float PunchOut = 0.08f, PunchHold = 0.05f, PunchBack = 0.16f;
    bool init;

    void Awake()
    {
        Figure = GetComponent<CharacterFigure>();
        noiseSeed = (Random.value * 1000f) * 0.173f; // animation noise only, no need to be deterministic
    }

    // Start a punch with hand 0 (left) or 1 (right). Returns the time to full extension.
    public float Punch(int hand)
    {
        punchStart[hand] = Time.time;
        return PunchOut;
    }

    // 0 at rest, 1 at full extension.
    public float PunchAmount(int hand)
    {
        float t = Time.time - punchStart[hand];
        if (t < 0f) return 0f;
        if (t < PunchOut) return t / PunchOut;
        if (t < PunchOut + PunchHold) return 1f;
        if (t < PunchOut + PunchHold + PunchBack) return 1f - (t - PunchOut - PunchHold) / PunchBack;
        return 0f;
    }

    void LateUpdate()
    {
        if (Figure == null) Figure = GetComponent<CharacterFigure>();
        var f = Figure;
        if (f == null || f.Hips == null) return;
        float dt = Time.deltaTime;
        float k = 1f - Mathf.Exp(-blend * dt);
        Vector3 hv = new Vector3(Velocity.x, 0f, Velocity.z);
        float speed = hv.magnitude;
        float rootYaw = transform.eulerAngles.y;
        if (!init) { bodyYaw = FollowLook ? LookYaw : rootYaw; init = true; }

        // ---------- body yaw ----------
        if (FollowLook)
        {
            // Moving: the body turns to the look. Standing: the head turns alone up to turnThreshold,
            // then drags the body along.
            // Fighting (guard up): squared up to the look, so the fists are where you're looking.
            float off = Mathf.DeltaAngle(bodyYaw, LookYaw);
            if (ArmMode == Arms.Aim && speed <= 0.3f)
            {
                // Aiming: the arm covers 70 degrees either side; past that the body turns to follow.
                if (Mathf.Abs(off) > 70f) aimTurning = true;
                if (aimTurning) bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, LookYaw, 240f * dt);
                if (Mathf.Abs(Mathf.DeltaAngle(bodyYaw, LookYaw)) < 3f) aimTurning = false;
            }
            else if (speed > 0.3f || ArmMode == Arms.Guard) bodyYaw = Mathf.MoveTowardsAngle(bodyYaw, LookYaw, 720f * dt);
            else if (Mathf.Abs(off) > turnThreshold) bodyYaw = LookYaw - Mathf.Sign(off) * turnThreshold;
        }
        else bodyYaw = rootYaw;
        if (CurrentPose == Pose.Seated || CurrentPose == Pose.Stunned || CurrentPose == Pose.Fallen) bodyYaw = rootYaw;

        // ---------- body (generated: from the DNA) ----------
        var dna = f.Generated && f.Plan != null ? f.Plan.dna : null;
        float heavy = dna != null ? Mathf.InverseLerp(1f, 3f, dna.fatFactor) : 0f;   // DEX 10 = 0, DEX 1 = 1
        float strS = dna != null ? dna.sheet.S : 0f;
        float jit = dna != null ? dna.jitter : 0f;
        Heavy = heavy; Jitter = jit;

        // ---------- gait ----------
        bool walking = Grounded && speed > 0.2f && (CurrentPose == Pose.Normal || CurrentPose == Pose.Dragging || CurrentPose == Pose.Dragged || CurrentPose == Pose.Cuffed);
        float run = Mathf.InverseLerp(2.5f, 6f, speed);
        // STR: longer strides at a slower cadence.
        phase += dt * (walking ? Mathf.Lerp(7f, 11f, run) * Mathf.Clamp(speed / 1.4f, 0.6f, 1.6f) * (1f - 0.12f * strS) : 0f);
        idle += dt * (1f + (FollowLook ? 0f : 1.5f * jit));  // twitchy bodies shift their weight faster (not the player: the camera rides the spine)
        float swing = walking ? Mathf.Lerp(28f, 55f, run) * (1f + 0.15f * strS) : 0f;
        float s = Mathf.Sin(phase), c = Mathf.Cos(phase);

        float hipL = -s * swing, hipR = s * swing;
        float kneeL = walking ? Mathf.Max(0f, c) * Mathf.Lerp(35f, 80f, run) : 3f;
        float kneeR = walking ? Mathf.Max(0f, -c) * Mathf.Lerp(35f, 80f, run) : 3f;
        float spinePitch = walking ? Mathf.Lerp(3f, 12f, run) : Mathf.Sin(idle * 1.3f) * 1.2f;
        float spineRoll = walking ? 0f : Mathf.Sin(idle * 0.9f) * 1.5f;
        float hipsY = 0f;
        if (walking) hipsY = -Mathf.Abs(c) * Mathf.Lerp(0.02f, 0.05f, run);
        float hipsPitch = 0f, hipsRoll = 0f;
        // Heavier bodies sway side to side as they walk and stand with their legs wider apart.
        if (walking) { spineRoll += s * heavy * 5f; hipsRoll += s * heavy * 3f; }
        float wide = (CurrentPose == Pose.Normal || CurrentPose == Pose.Cuffed || CurrentPose == Pose.Dragged) ? heavy * 5f : 0f;

        // Arms (lowered): opposite swing.
        float shL = s * swing * 0.7f, shR = -s * swing * 0.7f, elL = -12f, elR = -12f;
        float shLz = -6f, shRz = 6f;
        float pitchShare = Mathf.Clamp(LookPitch, -60f, 70f);

        switch (CurrentPose)
        {
            case Pose.Air: hipL = -25f; hipR = 10f; kneeL = 45f; kneeR = 30f; shLz = -25f; shRz = 25f; shL = -15f; shR = -15f; break;
            case Pose.Landing: hipL = hipR = -45f; kneeL = kneeR = 80f; hipsY = -0.2f; spinePitch = 18f; break;
            case Pose.Glide: hipL = hipR = 5f; kneeL = kneeR = 10f; shLz = -85f; shRz = 85f; shL = shR = 0f; elL = elR = 0f; spinePitch = 10f; break;
            case Pose.Hang: shL = shR = -175f; elL = elR = -10f; hipL = hipR = 5f; kneeL = kneeR = 15f; shLz = -8f; shRz = 8f; break;
            case Pose.Seated: hipL = hipR = -90f; kneeL = kneeR = 90f; hipsY = -0.25f; shL = shR = -25f; elL = elR = -50f; break;
            case Pose.Stunned:
            case Pose.Fallen:
                hipsPitch = -90f; hipsY = -0.55f; hipL = -5f; hipR = 8f; kneeL = 10f; kneeR = 4f; shLz = -40f; shRz = 35f; shL = shR = 0f; break;
            case Pose.Scooter: hipL = -8f; hipR = 8f; kneeL = kneeR = 18f; hipsY = -0.04f; shL = shR = -10f; break;
            case Pose.Staggered: spinePitch = -18f; hipL = -15f; kneeL = 20f; shLz = -35f; shRz = 35f; break;
            case Pose.Cuffing: shL = shR = -75f; elL = elR = -25f; shLz = 8f; shRz = -8f; spinePitch = 12f; break;
            case Pose.Dragging: shR = 40f; elR = -10f; shRz = 15f; shL = -10f; spinePitch = 6f; break;
            case Pose.Dragged: spinePitch = -10f; break;
        }

        // ---------- arm modes ----------
        var armMode = CurrentPose == Pose.Cuffed || CurrentPose == Pose.Dragged ? Arms.Behind : ArmMode;
        Quaternion aimL = Quaternion.identity, aimR = Quaternion.identity;
        if (armMode == Arms.Guard && CurrentPose != Pose.Hang && CurrentPose != Pose.Glide)
        {
            // Fists up by the chin, aimed with the look; punches extend along the arm.
            float aim = pitchShare * 0.7f;
            float pL = PunchAmount(0), pR = PunchAmount(1);
            // Guard: fists about a forearm in front of the face, just under the eye line.
            shL = Mathf.Lerp(-75f, -88f, pL) + aim; elL = Mathf.Lerp(-55f, -5f, pL); shLz = Mathf.Lerp(14f, 6f, pL);
            shR = Mathf.Lerp(-75f, -88f, pR) + aim; elR = Mathf.Lerp(-55f, -5f, pR); shRz = Mathf.Lerp(-14f, -6f, pR);
        }
        else if (armMode == Arms.Behind)
        {
            float strain = Straining ? Mathf.Sin(Time.time * 30f) * 6f : 0f;
            shL = 35f + strain; shR = 35f - strain; elL = elR = -70f; shLz = 22f; shRz = -22f;
        }

        // ---------- apply ----------
        var hips = f.Hips;
        float hipBase = f.HipHeight;
        hips.localPosition = Vector3.Lerp(hips.localPosition, new Vector3(0f, hipBase + hipsY * f.Scale, 0f), k);
        Quaternion hipsWorld = Quaternion.Euler(0f, bodyYaw, 0f) * Quaternion.Euler(hipsPitch, 0f, hipsRoll);
        hips.rotation = Quaternion.Slerp(hips.rotation, hipsWorld, k);

        // Spine takes some of the vertical look (the head the rest), and leans with speed.
        float spineFromLook = CurrentPose == Pose.Normal || CurrentPose == Pose.Air ? pitchShare * 0.3f : 0f;
        // Aiming: the shoulder takes up to 60 degrees of pitch, the body leans up to 20 more.
        if (ArmMode == Arms.Aim && (CurrentPose == Pose.Normal || CurrentPose == Pose.Air))
            spineFromLook = Mathf.Sign(LookPitch) * Mathf.Clamp(Mathf.Abs(LookPitch) - 60f, 0f, 20f);
        Set(f.Spine, Quaternion.Euler(spinePitch + spineFromLook, 0f, spineRoll), k);

        // Head: the rest of the look (yaw relative to the body, pitch).
        float headYaw = FollowLook ? Mathf.Clamp(Mathf.DeltaAngle(bodyYaw, LookYaw), -80f, 80f) : 0f;
        float headPitch = CurrentPose == Pose.Normal || CurrentPose == Pose.Air ? pitchShare * 0.7f : 0f;
        Set(f.Neck, Quaternion.Euler(headPitch, headYaw, 0f), k);

        Set(f.ShoulderL, Quaternion.Euler(shL, 0f, shLz), k);
        Set(f.ShoulderR, Quaternion.Euler(shR, 0f, shRz), k);
        Set(f.ElbowL, Quaternion.Euler(elL, 0f, 0f), k);
        Set(f.ElbowR, Quaternion.Euler(elR, 0f, 0f), k);
        Set(f.HipL, Quaternion.Euler(hipL, 0f, -wide), k);
        Set(f.HipR, Quaternion.Euler(hipR, 0f, wide), k);
        Set(f.KneeL, Quaternion.Euler(kneeL, 0f, 0f), k);
        Set(f.KneeR, Quaternion.Euler(kneeR, 0f, 0f), k);
        // Feet stay roughly level.
        Set(f.AnkleL, Quaternion.Euler(-(hipL + kneeL) * 0.5f, 0f, wide), k);
        Set(f.AnkleR, Quaternion.Euler(-(hipR + kneeR) * 0.5f, 0f, -wide), k);

        ExtraArms(f, s, swing, k);
        if (jit > 0f) JitterLayer(f, jit, dt);
    }

    // Arms beyond the first pair follow the main arm on their side (centred ones the right),
    // smaller and a beat behind.
    void ExtraArms(CharacterFigure f, float s, float swing, float k)
    {
        var extra = f.ExtraArms;
        if (extra == null || extra.Count == 0) return;
        if (extraShoulder == null || extraShoulder.Length != extra.Count)
        {
            extraShoulder = new Quaternion[extra.Count]; extraElbow = new Quaternion[extra.Count];
            for (int i = 0; i < extra.Count; i++) { extraShoulder[i] = Quaternion.identity; extraElbow[i] = Quaternion.identity; }
        }
        float lag = 1f - Mathf.Exp(-6f * Time.deltaTime);
        for (int i = 0; i < extra.Count; i++)
        {
            var a = extra[i];
            bool left = a.side < 0;
            Transform ms = left ? f.ShoulderL : f.ShoulderR, me = left ? f.ElbowL : f.ElbowR;
            float delayed = Mathf.Sin(phase - 0.9f - 0.4f * i) * swing * 0.25f * (left ? 1f : -1f);
            Quaternion ts = Quaternion.Slerp(Quaternion.identity, ms.localRotation, 0.6f) * Quaternion.Euler(delayed, 0f, 0f);
            Quaternion te = Quaternion.Slerp(Quaternion.identity, me.localRotation, 0.6f);
            extraShoulder[i] = Quaternion.Slerp(extraShoulder[i], ts, lag);
            extraElbow[i] = Quaternion.Slerp(extraElbow[i], te, lag);
            a.shoulder.localRotation = extraShoulder[i];
            if (a.elbow != null) a.elbow.localRotation = extraElbow[i];
        }
    }

    // DEX twitchiness: fast noise on the head and wrists and occasional quick glances.
    void JitterLayer(CharacterFigure f, float jit, float dt)
    {
        float t = Time.time * (9f + 6f * jit) + noiseSeed;
        float N(float o) => (Mathf.PerlinNoise(t, o) - 0.5f) * 2f;
        // Glances: every few seconds (more often when twitchier), a quick look aside.
        if (Time.time > nextGlance)
        {
            float r = Mathf.PerlinNoise(noiseSeed, Time.time);
            glanceYaw = (r < 0.5f ? -1f : 1f) * Mathf.Lerp(20f, 35f, r);
            glanceUntil = Time.time + Mathf.Lerp(0.3f, 0.6f, r);
            nextGlance = Time.time + Mathf.Lerp(4f, 1.2f, jit) * (0.6f + r);
        }
        float glance = Time.time < glanceUntil ? glanceYaw : 0f;
        var head = f.HeadJoint;
        if (head != null)
            head.localRotation = Quaternion.Euler(N(1.3f) * 4f * jit, N(2.7f) * 5f * jit + glance * jit, N(4.1f) * 3f * jit);
        if (f.WristL != null) f.WristL.localRotation = Quaternion.Euler(N(5.5f) * 10f * jit, N(6.2f) * 6f * jit, N(7.9f) * 8f * jit);
        if (f.WristR != null) f.WristR.localRotation = Quaternion.Euler(N(8.4f) * 10f * jit, N(9.6f) * 6f * jit, N(10.3f) * 8f * jit);
    }

    // Punches snap faster than the general blend.
    void Set(Transform j, Quaternion target, float k)
    {
        bool fast = ArmMode == Arms.Guard && (j == Figure.ShoulderL || j == Figure.ShoulderR || j == Figure.ElbowL || j == Figure.ElbowR);
        j.localRotation = Quaternion.Slerp(j.localRotation, target, fast ? Mathf.Max(k, 0.6f) : k);
    }
}
