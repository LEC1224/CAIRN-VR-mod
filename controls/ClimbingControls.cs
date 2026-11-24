using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnVR.Controls;

public sealed class ClimbingControls : MonoBehaviour
{
    readonly GamepadBridge gamepad = new();
    readonly ClimbingV2PawnLimb[] limbs = new ClimbingV2PawnLimb[4];
    readonly List<GameObject> pins = new(8);
    GameObject cue;
    Transform origin;
    Material ink;
    readonly LimbGazeHint gazeHint = new();
    readonly IdleLift[] lifts = { new(), new() };
    IntPtr motionLimb;
    bool hadSample, faulted;
    uint previousBumpers, consumedBumpers, previousHands;
    IntPtr climbingPawn;
    uint previousButtons;
    uint observedInputs;
    int scene = int.MinValue;
    float nextLookup;
    float recenterStart = -1;
    Action recenter;
    Vector3 gripStart, axisX, axisY;
    public ClimbingControls(IntPtr ptr) : base(ptr) { }

    public void Update()
    {
        if (faulted) return;
        try { Tick(); }
        catch (Exception e)
        {
            ControlsPlugin.Logger.LogError($"Disabled VR controls after a game input error: {e}");
            faulted = true;
            ResetTracking();
        }
    }
    [HideFromIl2Cpp]
    void Tick()
    {
        if (!Tracking.TryRead(out var sample)) { ResetTracking(); return; }
        if ((sample.Valid & ~observedInputs) != 0)
        {
            observedInputs |= sample.Valid;
            ControlsPlugin.Logger.LogInfo($"OpenXR inputs active. Eye gaze={(observedInputs & 1) != 0}, grip={(observedInputs & 2) != 0}, aim={(observedInputs & 4) != 0}, gamepad={(observedInputs & 8) != 0}.");
        }
        uint hands = (sample.TryGrip(false, out _) ? 1u : 0u) | (sample.TryGrip(true, out _) ? 2u : 0u);
        uint gripDown = hadSample ? sample.Pressed & ~previousButtons & hands & previousHands : 0;
        uint bumperDown = hadSample ? sample.Buttons & ~previousBumpers : 0;
        if ((previousHands & hands & 1) == 0) bumperDown &= ~Sample.BumperMask(false);
        if ((previousHands & hands & 2) == 0) bumperDown &= ~Sample.BumperMask(true);
        consumedBumpers &= sample.Buttons;
        previousHands = hands;
        previousButtons = sample.Pressed;
        previousBumpers = sample.Buttons;
        hadSample = true;

        if (!origin && Time.unscaledTime >= nextLookup)
        {
            origin = GameObject.Find("CairnVR.TrackingOrigin")?.transform;
            nextLookup = Time.unscaledTime + .4f;
        }
        if (!origin) { EndClimb(); gamepad.Apply(sample, null); return; }
        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (scene != activeScene) { scene = activeScene; ClearPins(); EndClimb(); }
        if (cue) cue!.SetActive(false);
        bool scouting = IsScouting();
        var pawn = PawnManager.IsReady ? PawnManager.Instance.ClimbingPawnController : null;
        bool climbing = pawn != null && pawn.Enabled && pawn.pawnReady && !pawn.IsInActivePause && !scouting
            && InputManager.IsReady && !InputManager.Instance.DisabledInputs && InputManager.Instance.climbingActionMap.enabled;
        if (climbing && pawn.Pointer != climbingPawn)
        {
            EndClimb();
            climbingPawn = pawn.Pointer;
            gripDown = bumperDown = 0;
        }
        else if (!climbing) EndClimb();
        bool recenterChord = ControlsPlugin.Recenter switch
        {
            ControlsPlugin.RecenterControl.BothGrips => !climbing && sample.Pressed == 3,
            ControlsPlugin.RecenterControl.BothStickClicks => (sample.Buttons & (3u << 8)) == (3u << 8),
            _ => false
        };
        if (recenterChord)
        {
            if (recenterStart < 0) recenterStart = Time.unscaledTime;
            if (Time.unscaledTime - recenterStart > .8f)
            {
                var method = Type.GetType("CairnVR.CameraRig.StereoView, CairnVR.Camera")?.GetMethod("Recenter");
                if (recenter == null && method != null) recenter = (Action)Delegate.CreateDelegate(typeof(Action), method);
                recenter?.Invoke();
                recenterStart = float.PositiveInfinity;
                EndClimb();
                gamepad.Apply(sample, null);
                return;
            }
        }
        else recenterStart = -1;

        if (ControlsPlugin.Markers && scouting && sample.TryAim(ControlsPlugin.MarkerRight, out var aim))
        {
            var ray = WorldRay(aim);
            if (Physics.Raycast(ray, out var hit, 300f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                PlaceCue(hit.point + .015f * hit.normal, ray.origin);
                if ((gripDown & (ControlsPlugin.MarkerRight ? 2u : 1u)) != 0)
                    TogglePin(hit.point + .03f * hit.normal, ray.origin);
            }
        }

        Vector2? motion = null;
        if (climbing)
        {
            UpdateGaze(pawn, sample, bumperDown);
            motion = UpdateReach(pawn, sample, gripDown);
        }
        // Keep a consumed press private until release, even if the gaze moves away.
        sample.Buttons &= ~consumedBumpers;
        gamepad.Apply(sample, motion);
    }

    [HideFromIl2Cpp]
    void UpdateGaze(ClimbingV2PawnController pawn, Sample sample, uint bumperDown)
    {
        if (ControlsPlugin.Gaze && (sample.Valid & 1) != 0 && pawn.CanSelectLimb())
        {
            var ray = WorldRay(sample.Gaze);
            var limb = Candidate(pawn, ray);
            if (limb != null)
            {
                bool right = limb.IsRight;
                uint hand = right ? 2u : 1u;
                if ((sample.FrameHands & hand) != 0 && sample.TryGrip(right, out _))
                {
                    PlaceCue(limb.CurrentIKTargetPosition, ray.origin);
                    gazeHint.Update(limb, right, limb.IsHand, ray.origin);
                    // Neither stock bumper action should open a menu over a targeted limb.
                    consumedBumpers |= bumperDown & (Sample.BumperMask(false) | Sample.BumperMask(true));
                    if ((bumperDown & Sample.BumperMask(right)) != 0)
                        pawn.StartReachingWithLimb(limb);
                    return;
                }
            }
        }
        gazeHint.Clear();
    }

    [HideFromIl2Cpp]
    Vector2? UpdateReach(ClimbingV2PawnController pawn, Sample sample, uint gripDown)
    {
        var module = pawn.CurrentModule;
        var reaching = pawn.ReachingModule;
        bool useMotion = ControlsPlugin.Reach || ControlsPlugin.Point;
        if (module != null && module.Pointer == pawn.IdleModule?.Pointer && useMotion)
        {
            motionLimb = IntPtr.Zero;
            // A deliberate lift starts a same-side reach.
            bool left = Lifted(sample, false);
            bool right = Lifted(sample, true);
            if (left != right && !pawn.FullManualSelectionMode && pawn.CanStartReaching() && pawn.CanSelectLimb())
            {
                var hand = right ? pawn.rightHand : pawn.leftHand;
                var foot = right ? pawn.rightFoot : pawn.leftFoot;
                var limb = LighterLimb(hand, foot);
                if (limb != null) pawn.StartReachingWithLimb(limb);
            }
            return null;
        }
        lifts[0].Reset();
        lifts[1].Reset();
        var active = module != null && module.Pointer == reaching?.Pointer ? reaching.ReachingLimb : null;
        if (active == null)
        {
            motionLimb = IntPtr.Zero;
            return null;
        }
        bool ownerRight = active.IsRight;
        uint owner = ownerRight ? 2u : 1u;
        if (!sample.TryGrip(ownerRight, out var grip))
        {
            motionLimb = IntPtr.Zero;
            return null;
        }
        if ((gripDown & owner) != 0)
        {
            reaching.TryGrabHold();
            motionLimb = IntPtr.Zero;
            return null;
        }
        if (!useMotion) return null;
        if (motionLimb != active.Pointer)
        {
            var basis = Camera.main;
            motionLimb = active.Pointer;
            gripStart = grip.Position;
            axisX = basis ? origin.InverseTransformDirection(basis.transform.right).normalized : Vector3.right;
            axisY = basis ? origin.InverseTransformDirection(basis.transform.up).normalized : Vector3.up;
        }
        Vector2 stick;
        if (ControlsPlugin.Point)
        {
            if (!sample.TryAim(ownerRight, out var aim)) return null;
            var ray = WorldRay(aim);
            if (!Physics.Raycast(ray, out var hit, 100f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                || hit.transform.IsChildOf(((Component)pawn).transform)) return null;
            var offset = origin.InverseTransformDirection(hit.point - active.CurrentIKTargetPosition);
            PlaceCue(hit.point + .01f * hit.normal, ray.origin);
            stick = new Vector2(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY)) / .25f;
        }
        else
        {
            var offset = grip.Position - gripStart;
            stick = new Vector2(Vector3.Dot(offset, axisX), Vector3.Dot(offset, axisY)) / ControlsPlugin.MotionRange;
        }
        // Stick input remains available for fine adjustments and accessibility.
        if (stick.sqrMagnitude < .0025f || new Vector2(sample.LX, sample.LY).sqrMagnitude > .04f) return null;
        return Vector2.ClampMagnitude(stick, 1f);
    }

    [HideFromIl2Cpp]
    bool Lifted(Sample sample, bool right)
    {
        int index = right ? 1 : 0;
        if (!sample.TryGrip(right, out var grip))
        {
            lifts[index].Reset();
            return false;
        }
        return lifts[index].MovedUp(grip.Position);
    }

    [HideFromIl2Cpp]
    static ClimbingV2PawnLimb LighterLimb(ClimbingV2PawnLimb hand, ClimbingV2PawnLimb foot)
    {
        if (hand != null && hand.IsIdle) return hand;
        if (foot != null && foot.IsIdle) return foot;
        if (hand == null || !hand.IsHolding) return foot != null && foot.IsHolding ? foot : null;
        if (foot == null || !foot.IsHolding) return hand;
        return hand.Weight <= foot.Weight ? hand : foot;
    }

    sealed class IdleLift
    {
        Vector3 rest;
        float stillSince;
        bool sampled, ready;

        public void Reset() { sampled = ready = false; }

        public bool MovedUp(Vector3 position)
        {
            float now = Time.unscaledTime;
            if (!sampled || (!ready && Vector3.Distance(rest, position) > .018f))
            {
                rest = position;
                stillSince = now;
                sampled = true;
                return false;
            }
            if (!ready)
            {
                ready = now - stillSince >= .20f;
                return false;
            }
            // Ignore slow drift and settle to a new baseline after downward motion.
            float rise = position.y - rest.y;
            rest = Vector3.Lerp(rest, position, 1f - Mathf.Exp(-Time.unscaledDeltaTime / .4f));
            if (rise < .05f) return false;
            Reset();
            return true;
        }
    }

    [HideFromIl2Cpp]
    void EndClimb()
    {
        if (climbingPawn == IntPtr.Zero) return;
        climbingPawn = motionLimb = IntPtr.Zero;
        lifts[0].Reset();
        lifts[1].Reset();
        gazeHint.Clear();
    }

    [HideFromIl2Cpp]
    Ray WorldRay(TrackedPose pose) => new(origin!.TransformPoint(pose.Position),
        origin.TransformDirection(pose.Rotation * Vector3.forward).normalized);
    [HideFromIl2Cpp]
    static bool IsScouting()
    {
        if (!CameraManager.IsReady) return false;
        return CameraManager.Instance.currentCameraType is CairnCamera.CairnCameraType.WalkingEagleEyeView
            or CairnCamera.CairnCameraType.WalkingEagleEyePlusView or CairnCamera.CairnCameraType.WalkingEagleEyeViewPath
            or CairnCamera.CairnCameraType.ClimbingEagleEyeView or CairnCamera.CairnCameraType.ClimbingEagleEyePlusView
            or CairnCamera.CairnCameraType.ClimbingEagleEyeFreeView or CairnCamera.CairnCameraType.ClimbingEagleEyeViewPath;
    }
    [HideFromIl2Cpp]
    ClimbingV2PawnLimb Candidate(ClimbingV2PawnController pawn, Ray ray)
    {
        limbs[0] = pawn.leftHand;
        limbs[1] = pawn.rightHand;
        limbs[2] = pawn.leftFoot;
        limbs[3] = pawn.rightFoot;
        float best = float.PositiveInfinity, next = float.PositiveInfinity;
        ClimbingV2PawnLimb winner = null;
        foreach (var limb in limbs)
        {
            if (limb == null) continue;
            Vector3 offset = limb.CurrentIKTargetPosition - ray.origin;
            float distance = offset.magnitude;
            if (distance < .05f || Vector3.Dot(ray.direction, offset) <= 0) continue;
            float angle = Vector3.Angle(ray.direction, offset);
            if (angle > 3.5f) continue;
            if (Physics.Raycast(ray.origin, offset.normalized, out var obstruction, Mathf.Max(0, distance - .1f),
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && !obstruction.transform.IsChildOf(((Component)pawn).transform)) continue;
            if (angle < best) { next = best; best = angle; winner = limb; }
            else next = Mathf.Min(next, angle);
        }
        return best <= 3f && next - best >= .45f ? winner : null;
    }
    [HideFromIl2Cpp]
    GameObject Dot(string name)
    {
        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        dot.name = name; dot.layer = 2; // Ignore Raycast
        var collider = dot.GetComponent<Collider>(); collider.enabled = false; Object.Destroy(collider);
        if (!ink)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            if (shader) { ink = new Material(shader); ink.color = new Color(.35f, .95f, 1f); }
        }
        if (ink) dot.GetComponent<Renderer>().sharedMaterial = ink;
        return dot;
    }
    [HideFromIl2Cpp]
    void PlaceCue(Vector3 pos, Vector3 eye)
    {
        if (!cue) cue = Dot("Cairn VR target");
        var direction = eye - pos;
        float size = Mathf.Clamp(direction.magnitude * .009f, .018f, .15f);
        cue!.transform.position = pos + direction.normalized * size;
        cue.transform.localScale = Vector3.one * size;
        cue.SetActive(true);
    }
    [HideFromIl2Cpp]
    void TogglePin(Vector3 point, Vector3 eye)
    {
        for (int i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (pin && Vector3.Distance(pin.transform.position, point) < .18f)
            {
                Object.Destroy(pin);
                pins.RemoveAt(i);
                return;
            }
        }
        if (pins.Count == 8)
        {
            Object.Destroy(pins[0]);
            pins.RemoveAt(0);
        }
        var marker = Dot("Cairn VR route mark");
        marker.transform.position = point;
        marker.transform.localScale = Vector3.one * Mathf.Clamp(Vector3.Distance(point, eye) * .012f, .06f, .35f);
        pins.Add(marker);
    }
    [HideFromIl2Cpp]
    void ClearPins()
    {
        foreach (var pin in pins) if (pin) Object.Destroy(pin);
        pins.Clear();
    }
    [HideFromIl2Cpp]
    void ResetTracking()
    {
        if (!hadSample) return;
        EndClimb();
        hadSample = false;
        previousButtons = previousBumpers = consumedBumpers = previousHands = 0;
        recenterStart = -1;
        if (cue) cue!.SetActive(false);
        gamepad.Clear();
    }
    public void OnDestroy()
    {
        gazeHint.Dispose();
        ControllerModels.Stop();
        gamepad.Dispose(); ClearPins();
        Application.runInBackground = ControlsPlugin.PreviouslyRanInBackground;
        if (cue) Object.Destroy(cue); if (ink) Object.Destroy(ink);
    }
}
