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
    bool clutch, hadSample, faulted;
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
        if (!hadSample) previousButtons = sample.Pressed; // tracking recovery requires a fresh press
        hadSample = true;
        var down = sample.Pressed & ~previousButtons;
        previousButtons = sample.Pressed;
        if (!origin && Time.unscaledTime >= nextLookup)
        {
            origin = GameObject.Find("CairnVR.TrackingOrigin")?.transform;
            nextLookup = Time.unscaledTime + .4f;
        }
        if (!origin) { gamepad.Apply(sample, null); return; }
        var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
        if (scene != activeScene) { scene = activeScene; ClearPins(); clutch = false; }
        if (cue) cue!.SetActive(false);
        bool recenterChord = ControlsPlugin.Recenter switch
        {
            ControlsPlugin.RecenterControl.BothGrips => sample.Pressed == 3,
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
            }
            clutch = false;
            gamepad.Apply(sample, null);
            return;
        }
        recenterStart = -1;

        bool limbControls = ControlsPlugin.Gaze || ControlsPlugin.Reach || ControlsPlugin.Point;
        if (!(limbControls || ControlsPlugin.Markers)) { gamepad.Apply(sample, null); return; }
        bool scouting = IsScouting();
        var pawn = limbControls && PawnManager.IsReady ? PawnManager.Instance.ClimbingPawnController : null;
        bool climbing = pawn != null && pawn.Enabled && pawn.pawnReady && !pawn.IsInActivePause && !scouting
            && InputManager.IsReady && !InputManager.Instance.DisabledInputs && InputManager.Instance.climbingActionMap.enabled;

        if (ControlsPlugin.Markers && scouting && (sample.Valid & 4) != 0)
        {
            var ray = WorldRay(sample.Aim);
            if (Physics.Raycast(ray, out var hit, 300f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                PlaceCue(hit.point + .015f * hit.normal, ray.origin);
                if ((down & 2) != 0) TogglePin(hit.point + .03f * hit.normal, ray.origin);
            }
        }
        if (ControlsPlugin.Gaze && climbing && (sample.Valid & 1) != 0 && pawn!.CanSelectLimb())
        {
            var ray = WorldRay(sample.Gaze);
            var limb = Candidate(pawn, ray);
            if (limb != null)
            {
                PlaceCue(limb.CurrentIKTargetPosition, ray.origin);
                if ((down & 1) != 0) pawn.StartReachingWithLimb(limb);
            }
        }

        Vector2? motion = null;
        bool canReach = (ControlsPlugin.Reach || ControlsPlugin.Point) && climbing && (sample.Valid & 2) != 0 &&
            pawn!.CurrentModule != null && pawn.CurrentModule.Pointer == pawn.ReachingModule.Pointer;
        if (!canReach || (sample.Pressed & 2) == 0) clutch = false;
        else
        {
            if ((down & 2) != 0)
            {
                var basis = Camera.main;
                gripStart = sample.Grip.Position;
                axisX = basis ? origin!.InverseTransformDirection(basis.transform.right).normalized : Vector3.right;
                axisY = basis ? origin!.InverseTransformDirection(basis.transform.up).normalized : Vector3.up;
                clutch = true;
            }
            if (clutch && ControlsPlugin.Point)
            {
                // Project the direction towards the visible surface into the same screen axes
                // used by the gamepad. CAIRN still owns IK, hold acceptance, and contact.
                motion = Vector2.zero;
                var reaching = pawn.ReachingModule.ReachingLimb;
                if ((sample.Valid & 4) != 0 && reaching != null)
                {
                    var ray = WorldRay(sample.Aim);
                    if (Physics.Raycast(ray, out var hit, 100f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                        && !hit.transform.IsChildOf(((Component)pawn).transform))
                    {
                        var offset = hit.point - reaching.CurrentIKTargetPosition;
                        PlaceCue(hit.point + .01f * hit.normal, ray.origin);
                        var horizontal = origin.TransformDirection(axisX);
                        var vertical = origin.TransformDirection(axisY);
                        var steer = new Vector2(Vector3.Dot(offset, horizontal), Vector3.Dot(offset, vertical));
                        motion = steer.magnitude < .018f ? Vector2.zero : Vector2.ClampMagnitude(steer / .25f, 1);
                    }
                }
            }
            else if (clutch)
            {
                var delta = sample.Grip.Position - gripStart;
                var stick = new Vector2(Vector3.Dot(delta, axisX), Vector3.Dot(delta, axisY)) / ControlsPlugin.MotionRange;
                motion = stick.sqrMagnitude < .0025f ? Vector2.zero : Vector2.ClampMagnitude(stick, 1);
            }
        }
        gamepad.Apply(sample, motion);
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
        hadSample = false; clutch = false; previousButtons = 0; recenterStart = -1;
        if (cue) cue!.SetActive(false);
        gamepad.Clear();
    }
    public void OnDestroy()
    {
        ControllerModels.Stop();
        gamepad.Dispose(); ClearPins();
        Application.runInBackground = ControlsPlugin.PreviouslyRanInBackground;
        if (cue) Object.Destroy(cue); if (ink) Object.Destroy(ink);
    }
}
