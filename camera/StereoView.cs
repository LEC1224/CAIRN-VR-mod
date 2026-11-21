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
    internal static ConfigEntry<bool> SteadyFalls, FollowAudio, FlipY;
    internal static string BinaryDirectory;

    public override void Load()
    {
        if (Environment.GetEnvironmentVariable("CAIRN_VR") != "1") return;
        Logger = Log;
        RenderScale = Config.Bind("Display", "RenderScale", 1f, "Multiplier for each runtime-recommended eye resolution (0.4 to 1.5).");
        SteadyFalls = Config.Bind("Comfort", "StableFalls", true, "Hold the viewing position during falls and rewinds, then fade through black to the new position.");
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
    [DllImport(Dll)] internal static extern void vr_abort();
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
    private float nextAttempt, nextLookup, blinkUntil;
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
        if (!opened && Time.realtimeSinceStartup < nextAttempt) return;
        try
        {
            if (!opened && !Open()) return;
            var result = Native.vr_frame(tracking);
            if (result < 0) throw new InvalidOperationException(Native.Error());
            if (result == 0) return;
            try
            {
                var source = Camera.main;
                if (!source || !source.enabled) source = FindSceneCamera();
                var blackout = UpdateTracking(source);
                for (var i = 0; i < eyes.Length; i++) RenderEye(i, source, blackout);
                int token = Native.vr_queue(textures[0], textures[1]);
                if (token < 0) throw new InvalidOperationException("The stereo frame could not be queued.");
                GL.IssuePluginEvent(submit, token);
                if (!reportedFrame)
                {
                    CameraPlugin.Logger.LogInfo($"Stereo frame queued. Source camera: {(source ? source.name : "UI only")}.");
                    reportedFrame = true;
                }
            }
            catch { Native.vr_abort(); throw; }
        }
        catch (Exception ex)
        {
            Report(ex.ToString());
            Close();
            nextAttempt = Time.realtimeSinceStartup + 3;
        }
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

    private bool UpdateTracking(Camera source)
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
                anchor = target.position;
                hasAnchor = true;
                sceneHandle = handle;
            }
        }
        else if (!hasAnchor) anchorRotation = Quaternion.identity;
        wasFalling = falling;
        var yaw = anchorRotation * neutralRotation;
        origin.SetPositionAndRotation(anchor - yaw * neutralHead, yaw);
        if (CameraPlugin.FollowAudio.Value && listener && source)
        {
            var audio = listener.AudioListenerTransform;
            if (audio && audio != source.transform && !source.transform.IsChildOf(audio)) audio.rotation = yaw * headRotation;
        }
        return now < blinkUntil;
    }

    private void RenderEye(int index, Camera source, bool blackout)
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
            if (blackout)
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
        }
        finally { GL.invertCulling = oldCulling; RenderTexture.active = oldTarget; }
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
        opened = false; centered = false; hasAnchor = false; wasFalling = false; reportedFrame = false;
        if (origin) Object.Destroy(origin.gameObject);
        foreach (var image in images) if (image) { image.Release(); Object.Destroy(image); }
    }
}


