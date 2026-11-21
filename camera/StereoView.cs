using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine.InputSystem;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using Object = UnityEngine.Object;

namespace CairnVR.CameraRig;

[BepInPlugin("community.cairnvr.camera", "CAIRN VR Camera", "1.0.0")]
public sealed class CameraPlugin : BasePlugin
{
    internal static ManualLogSource Logger;
    internal static ConfigEntry<float> RenderScale;
    internal static ConfigEntry<bool> SteadyFalls, FollowAudio, FlipY, LeanFade, LookAssist, StickRecenter;
    internal static string BinaryDirectory;

    public override void Load()
    {
        if (Environment.GetEnvironmentVariable("CAIRN_VR") != "1") return;
        Logger = Log;
        RenderScale = Config.Bind("Display", "RenderScale", 1f, "Multiplier for each runtime-recommended eye resolution (0.4 to 1.5).");
        SteadyFalls = Config.Bind("Comfort", "StableFalls", true, "Hold the viewing position during falls and rewinds, then fade through black to the new position.");
        LeanFade = Config.Bind("Comfort", "FadeNearRock", true, "Darken the view as a physical head lean reaches solid scenery.");
        LookAssist = Config.Bind("Comfort", "KeepClimberInView", true, "Gently follow steep camera aim so the climber stays in view. Turning this off keeps neutral pitch level.");
        StickRecenter = Config.Bind("Comfort", "GamepadRecenter", true, "Hold both thumbstick clicks for 0.8 seconds to recenter the headset.");
        FollowAudio = Config.Bind("Audio", "HeadOrientation", true, "Orient the game's spatial listener with the headset when it has its own transform.");
        FlipY = Config.Bind("Display", "FlipEyeTextures", true, "Flip Unity's offscreen projection for direct D3D11 texture submission.");
        BinaryDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        NativeLibrary.Load(Path.Combine(BinaryDirectory, "openxr_loader.dll"));
        NativeLibrary.Load(Path.Combine(BinaryDirectory, "CairnVRStereo.dll"));
        AddComponent<StereoView>();
        Log.LogInfo("CAIRN VR camera loaded. Waiting for the local OpenXR runtime.");
    }
}

internal static class Native
{
    internal const string Dll = "CairnVRStereo";
    [StructLayout(LayoutKind.Sequential)]
    internal struct Display { public int leftWidth, rightWidth, leftHeight, rightHeight, format; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Tracking
    {
        public float x, y, z, w, px, py, pz, left, right, up, down;
        public Vector3 Position => new(px, py, -pz);
        public Quaternion Rotation => new(-x, -y, z, w);
    }
    [DllImport(Dll)] internal static extern int vr_open(IntPtr texture, float scale, out Display display);
    [DllImport(Dll)] internal static extern int vr_frame([Out] Tracking[] views);
    [DllImport(Dll)] internal static extern int vr_queue(IntPtr left, IntPtr right);
    [DllImport(Dll)] internal static extern IntPtr vr_submit_event();
    [DllImport(Dll)] internal static extern void vr_counts(out uint drawn, out uint empty);
    [DllImport(Dll)] internal static extern void vr_tracking_status(out int state, out uint notRequested, out uint tracking, out uint flags);
    [DllImport(Dll)] internal static extern void vr_close();
    [DllImport(Dll, CharSet=CharSet.Ansi)] private static extern void vr_error(StringBuilder error, int size);
    internal static string Error() { var value = new StringBuilder(512); vr_error(value, value.Capacity); return value.ToString(); }
}

public sealed class StereoView : MonoBehaviour
{
    public StereoView(IntPtr ptr) : base(ptr) { }
    public static event Action<Camera> BeforeEyeRender, AfterEyeRender;
    public static void Recenter() => recenterRequested = true;
    private static bool recenterRequested;
    private readonly Camera[] eyes = new Camera[2];
    private readonly RenderTexture[] images = new RenderTexture[2];
    private readonly IntPtr[] textures = new IntPtr[2];
    private readonly Native.Tracking[] tracking = new Native.Tracking[2];
    private Transform origin;
    private IntPtr submit;
    private float nextAttempt, nextLookup, blinkUntil, lastFrame, focusElevation, stickHeldSince = -1;
    private float nextFrameReport, nextStateCheck;
    private int lastState;
    private int collisionLayers = Physics.DefaultRaycastLayers;
    private Material fadeMaterial;
    private Transform adjustedListener;
    private Quaternion originalAudio, adjustedAudio;
    private bool opened, centered, wasFalling, hasAnchor, reportedFrame;
    private string previousError;
    private Vector3 neutralHead, anchor;
    private Quaternion neutralRotation, anchorRotation;
    private PawnControllerSwitcher pawn;
    private AudioListenerController listener;
    private int sceneHandle;

    private void LateUpdate()
    {
        if (Application.isFocused && Keyboard.current?.f8Key.wasPressedThisFrame == true) Recenter();
        var pad = Gamepad.current;
        if (CameraPlugin.StickRecenter.Value && Application.isFocused && pad != null &&
            pad.leftStickButton.isPressed && pad.rightStickButton.isPressed)
        {
            if (stickHeldSince < 0) stickHeldSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - stickHeldSince >= .8f)
            {
                Recenter();
                stickHeldSince = float.PositiveInfinity;
            }
        }
        else stickHeldSince = -1;
        if (!opened && Time.realtimeSinceStartup < nextAttempt) return;
        try
        {
            if (!opened && !Open()) return;
            ReportState();
            var result = Native.vr_frame(tracking);
            if (result < 0) throw new InvalidOperationException(Native.Error());
            if (result == 0) return;
            if (result == 2)
            {
                QueueFrame(IntPtr.Zero, IntPtr.Zero);
                return;
            }
            var source = Camera.main;
            if (!source || !source.enabled) source = FindSceneCamera();
            var fade = UpdateTracking(source);
            for (var i = 0; i < eyes.Length; i++) RenderEye(i, source, fade);
            QueueFrame(textures[0], textures[1]);
            lastFrame = Time.realtimeSinceStartup;
            if (!reportedFrame)
            {
                CameraPlugin.Logger.LogInfo($"Stereo frame queued. Source camera: {(source ? source.name : "UI only")}.");
                reportedFrame = true;
            }
        }
        catch (Exception ex)
        {
            Report(ex.ToString());
            Close();
            nextAttempt = Time.realtimeSinceStartup + 3;
        }
    }

    private void QueueFrame(IntPtr left, IntPtr right)
    {
        int token = Native.vr_queue(left, right);
        if (token < 0) throw new InvalidOperationException("The OpenXR frame could not be queued.");
        GL.IssuePluginEvent(submit, token);
    }

    private void ReportState()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextStateCheck) return;
        nextStateCheck = now + 1f;
        Native.vr_tracking_status(out int state, out uint notRequested, out uint tracking, out uint flags);
        if (state == lastState && now < nextFrameReport) return;
        Native.vr_counts(out var drawn, out var empty);
        var name = state switch
        {
            1 => "Idle", 2 => "Ready", 3 => "Synchronized", 4 => "Visible", 5 => "Focused",
            6 => "Stopping", 7 => "LossPending", 8 => "Exiting", _ => "Unknown"
        };
        CameraPlugin.Logger.LogInfo($"OpenXR {name}: {drawn} stereo and {empty} empty frames accepted. " +
            $"Runtime requested no rendering {notRequested} times; incomplete tracking {tracking} times. Last view flags 0x{flags:X}.");
        if (now >= nextFrameReport) nextFrameReport = now + 60f;
        lastState = state;
    }

    private Camera FindSceneCamera()
    {
        Camera best = null;
        foreach (var cam in Camera.allCameras)
        {
            if (!cam || cam.targetTexture || !cam.enabled || cam.cameraType != CameraType.Game) continue;
            if (!best || cam.depth < best.depth) best = cam;
        }
        return best;
    }

    private bool Open()
    {
        nextAttempt = Time.realtimeSinceStartup + 3;
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11)
        {
            Report("CAIRN VR needs Direct3D 11. Launch with -force-d3d11.");
            return false;
        }
        if (Native.vr_open(Texture2D.whiteTexture.GetNativeTexturePtr(), Mathf.Clamp(CameraPlugin.RenderScale.Value, .4f, 1.5f), out var display) == 0)
        {
            Report(Native.Error());
            return false;
        }
        opened = true;
        nextFrameReport = Time.realtimeSinceStartup + 8f;
        nextStateCheck = 0;
        lastState = 0;
        submit = Native.vr_submit_event();
        origin = new GameObject("CairnVR.TrackingOrigin").transform;
        Object.DontDestroyOnLoad(origin.gameObject);
        var format = display.format switch { 29 => GraphicsFormat.R8G8B8A8_SRGB, 87 => GraphicsFormat.B8G8R8A8_UNorm, 91 => GraphicsFormat.B8G8R8A8_SRGB, _ => GraphicsFormat.R8G8B8A8_UNorm };
        for (var i = 0; i < 2; i++)
        {
            var go = new GameObject(i == 0 ? "CairnVR.LeftEye" : "CairnVR.RightEye");
            go.transform.SetParent(origin, false);
            eyes[i] = go.AddComponent<Camera>();
            eyes[i].enabled = false;
            images[i] = new RenderTexture(i == 0 ? display.leftWidth : display.rightWidth, i == 0 ? display.leftHeight : display.rightHeight, 24, format);
            if (!images[i].Create()) throw new InvalidOperationException("Unity could not allocate the OpenXR eye texture.");
            textures[i] = images[i].GetNativeTexturePtr();
        }
        previousError = null;
        CameraPlugin.Logger.LogInfo($"OpenXR stereo session created, left eye {display.leftWidth}x{display.leftHeight}, right eye {display.rightWidth}x{display.rightHeight}.");
        return true;
    }

    private void OnGUI()
    {
        if (opened && reportedFrame && Time.realtimeSinceStartup - lastFrame < 3f) return;
        string status = SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11
            ? "Direct3D 11 is required. Relaunch through the Steam VR option."
            : "Waiting for headset tracking. Connect or wake the headset and start your OpenXR runtime.";
        if (opened && lastState == 3)
            status = "The headset is connected. Put it on and return to CAIRN in the headset.";
        GUI.Box(new Rect(12, 12, Mathf.Min(580, Screen.width - 24), 54), "CAIRN VR\n" + status);
    }

    private float UpdateTracking(Camera source)
    {
        float now = Time.realtimeSinceStartup;
        var head = (tracking[0].Position + tracking[1].Position) * .5f;
        var headRotation = Quaternion.Slerp(tracking[0].Rotation, tracking[1].Rotation, .5f);
        if (!centered || recenterRequested)
        {
            neutralHead = head;
            neutralRotation = Quaternion.Inverse(Quaternion.Euler(0, headRotation.eulerAngles.y, 0));
            recenterRequested = false;
            centered = true;
        }
        if (now >= nextLookup)
        {
            nextLookup = now + 1f;
            if (!pawn) pawn = Object.FindObjectOfType<PawnControllerSwitcher>();
            if (!listener && CameraPlugin.FollowAudio.Value) listener = Object.FindObjectOfType<AudioListenerController>();
            var cameraManager = CameraManager.IsReady ? CameraManager.Instance : null;
            var gameRig = cameraManager ? cameraManager.CurrentCairnCamera : null;
            collisionLayers = gameRig && gameRig.collisionLayers != 0 ? gameRig.collisionLayers : Physics.DefaultRaycastLayers;
        }
        bool falling = false;
        if (CameraPlugin.SteadyFalls.Value && pawn && pawn.isActiveAndEnabled)
        {
            var climb = pawn.climbingPawnController;
            var rewind = pawn.Rewind;
            falling = (climb && climb.isActiveAndEnabled && (climb.IsFalling || climb.IsSecureFalling)) || (rewind && rewind.IsRewinding);
        }
        if (source)
        {
            var target = source.transform;
            int handle = source.gameObject.scene.handle;
            if (!hasAnchor || !falling || handle != sceneHandle)
            {
                if (hasAnchor && (wasFalling || handle != sceneHandle || (anchor - target.position).sqrMagnitude > 16f))
                    blinkUntil = now + .12f;
                var forward = Vector3.ProjectOnPlane(target.forward, Vector3.up);
                if (forward.sqrMagnitude > .001f) anchorRotation = Quaternion.LookRotation(forward, Vector3.up);
                var elevation = CameraPlugin.LookAssist.Value ? FramingElevation(source) : 0f;
                focusElevation = hasAnchor && handle == sceneHandle
                    ? Mathf.MoveTowards(focusElevation, elevation, Time.unscaledDeltaTime * 75f) : elevation;
                anchor = target.position;
                hasAnchor = true;
                sceneHandle = handle;
            }
        }
        else if (!hasAnchor) anchorRotation = Quaternion.identity;
        wasFalling = falling;
        var yaw = anchorRotation * neutralRotation;
        var direction = yaw * headRotation * Vector3.forward;
        var right = Vector3.Cross(Vector3.up, direction);
        // Only support the authored pitch when facing along it. A sideways glance
        // keeps the horizon level, and physical translations always follow gravity.
        var aimWeight = Vector3.Dot(direction, anchorRotation * Vector3.forward);
        var basis = right.sqrMagnitude > .0001f
            ? Quaternion.AngleAxis(-focusElevation * aimWeight, right.normalized) * yaw : yaw;
        var worldHead = anchor + yaw * (head - neutralHead);
        origin.SetPositionAndRotation(worldHead - basis * head, basis);
        if (CameraPlugin.FollowAudio.Value && listener && source)
        {
            var audio = listener.AudioListenerTransform;
            if (listener.isActiveAndEnabled && audio && audio != source.transform && !source.transform.IsChildOf(audio))
            {
                if (adjustedListener != audio) RestoreAudio();
                if (!adjustedListener || Quaternion.Angle(audio.rotation, adjustedAudio) > .01f)
                    originalAudio = audio.rotation;
                adjustedListener = audio;
                adjustedAudio = basis * headRotation;
                audio.rotation = adjustedAudio;
            }
            else RestoreAudio();
        }
        else RestoreAudio();
        if (now < blinkUntil) return Mathf.Clamp01((blinkUntil - now) / .08f);
        return source && CameraPlugin.LeanFade.Value ? LeanOpacity(worldHead) : 0f;
    }

    private float FramingElevation(Camera source)
    {
        var direction = source.transform.forward;
        if (pawn && pawn.isActiveAndEnabled)
        {
            var climb = pawn.climbingPawnController;
            var walk = pawn.walkingPawnController;
            var body = climb && climb.isActiveAndEnabled ? climb.bodyHandler : null;
            Vector3? aim = body != null ? body.BodyPosition :
                walk && walk.isActiveAndEnabled ? ((Component)walk).transform.position + Vector3.up : null;
            if (aim.HasValue)
            {
                var viewport = source.WorldToViewportPoint(aim.Value);
                if (viewport.z > .1f && viewport.x > 0 && viewport.x < 1 && viewport.y > 0 && viewport.y < 1)
                    direction = (aim.Value - source.transform.position).normalized;
            }
        }
        var elevation = Mathf.Asin(Mathf.Clamp(direction.y, -1, 1)) * Mathf.Rad2Deg;
        float fov = elevation > 0 ? Mathf.Min(tracking[0].up, tracking[1].up) : -Mathf.Max(tracking[0].down, tracking[1].down);
        var margin = Mathf.Clamp(fov * Mathf.Rad2Deg * .40f, 3f, 20f);
        return Mathf.Sign(elevation) * Mathf.Max(0, Mathf.Abs(elevation) - margin);
    }

    private float LeanOpacity(Vector3 worldHead)
    {
        var offset = worldHead - anchor;
        float distance = offset.magnitude;
        if (distance < .025f) return 0f;
        const float clearance = .14f;
        if (Physics.SphereCast(anchor, .065f, offset / distance, out var obstacle,
            distance + clearance, collisionLayers, QueryTriggerInteraction.Ignore))
            return 1 - Mathf.Clamp01((obstacle.distance - distance) / clearance);
        return Physics.CheckSphere(worldHead, .065f, collisionLayers, QueryTriggerInteraction.Ignore) ? 1f : 0f;
    }

    private void RestoreAudio()
    {
        if (adjustedListener && Quaternion.Angle(adjustedListener.rotation, adjustedAudio) <= .01f)
            adjustedListener.rotation = originalAudio;
        adjustedListener = null;
    }

    private void RenderEye(int index, Camera source, float fade)
    {
        var eye = eyes[index];
        var view = tracking[index];
        if (source) eye.CopyFrom(source);
        eye.enabled = false;
        eye.stereoTargetEye = StereoTargetEyeMask.None;
        eye.targetTexture = images[index];
        eye.rect = new Rect(0, 0, 1, 1);
        eye.allowMSAA = false;
        eye.orthographic = false;
        eye.nearClipPlane = .045f;
        eye.transform.SetLocalPositionAndRotation(view.Position, view.Rotation);
        eye.ResetWorldToCameraMatrix();
        eye.ResetCullingMatrix();
        var near = eye.nearClipPlane;
        var p = Matrix4x4.Frustum(Mathf.Tan(view.left)*near, Mathf.Tan(view.right)*near, Mathf.Tan(view.down)*near, Mathf.Tan(view.up)*near, near, eye.farClipPlane);
        bool flip = CameraPlugin.FlipY.Value;
        eye.projectionMatrix = flip ? Matrix4x4.Scale(new Vector3(1,-1,1)) * p : p;
        bool oldCulling = GL.invertCulling;
        var oldTarget = RenderTexture.active;
        try
        {
            if (fade >= .999f)
            {
                RenderTexture.active = images[index];
                GL.Clear(true,true,Color.black);
                return;
            }
            BeforeEyeRender?.Invoke(eye);
            GL.invertCulling = flip != oldCulling;
            eye.Render();
            GL.invertCulling = oldCulling;
            AfterEyeRender?.Invoke(eye);
            if (fade > .001f) DrawFade(images[index], fade);
        }
        finally { GL.invertCulling = oldCulling; RenderTexture.active = oldTarget; }
    }

    private void DrawFade(RenderTexture target, float opacity)
    {
        if (!fadeMaterial)
        {
            var shader = Shader.Find("UI/Default");
            if (!shader) return;
            fadeMaterial = new Material(shader);
            fadeMaterial.mainTexture = Texture2D.whiteTexture;
        }
        RenderTexture.active = target;
        GL.PushMatrix();
        try
        {
            GL.LoadOrtho();
            if (!fadeMaterial.SetPass(0)) return;
            GL.Begin(GL.QUADS);
            GL.Color(new Color(0, 0, 0, opacity));
            GL.Vertex3(0, 0, 0); GL.Vertex3(0, 1, 0);
            GL.Vertex3(1, 1, 0); GL.Vertex3(1, 0, 0);
            GL.End();
        }
        finally { GL.PopMatrix(); }
    }

    private void Report(string message)
    {
        if (previousError == message) return;
        previousError = message;
        CameraPlugin.Logger.LogWarning(message);
    }

    private void OnDestroy() => Close();
    private void Close()
    {
        if (opened) Native.vr_close();
        RestoreAudio();
        if (fadeMaterial) Object.Destroy(fadeMaterial);
        opened = false; centered = false; hasAnchor = false; wasFalling = false; reportedFrame = false;
        if (origin) Object.Destroy(origin.gameObject);
        foreach (var image in images) if (image) { image.Release(); Object.Destroy(image); }
    }
}


