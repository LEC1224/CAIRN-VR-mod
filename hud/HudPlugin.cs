using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnVR.Hud;

[BepInPlugin("local.cairnvr.hud", "CAIRN VR menus and subtitles", "1.1.0")]
public sealed class HudPlugin : BasePlugin
{
    private const float PanelDistance = 2.4f;
    private const float HalfWidth = 1.4f;
    private const float HalfHeight = HalfWidth * 900f / 1600f;
    private Camera captureCamera;
    private RenderTexture surface;
    private Func<Pose> trackedHeadPose;
    private Canvas[] canvases = Array.Empty<Canvas>();
    private readonly List<(Canvas canvas, RenderMode mode, Camera camera, float distance)> redirected = new();
    private int capturedFrame = -1;
    private float nextDiscovery;
    private int lastCanvasCount = -1;
    private int captures;
    private readonly string captureFolder = Environment.GetEnvironmentVariable("CAIRNVR_CAPTURE");

    public override void Load()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetType("CairnVR.CameraRig.StereoView") != null);
        if (assembly == null)
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            return;
        }
        Subscribe(assembly);
    }

    private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
    {
        if (args.LoadedAssembly.GetType("CairnVR.CameraRig.StereoView") == null) return;
        Subscribe(args.LoadedAssembly);
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
    }

    private void Subscribe(Assembly assembly)
    {
        var type = assembly.GetType("CairnVR.CameraRig.StereoView", true);
        trackedHeadPose = type.GetProperty("TrackedHeadPose").GetMethod.CreateDelegate<Func<Pose>>();
        type.GetEvent("AfterEyeRender").AddEventHandler(null, (Action<Camera>)Composite);
        Log.LogInfo("Attached game UI to stereo rendering.");
    }

    private void EnsureCamera()
    {
        if (captureCamera != null) return;
        var holder = new GameObject("CairnVR.UI.Capture");
        Object.DontDestroyOnLoad(holder);
        captureCamera = holder.AddComponent<Camera>();
        captureCamera.enabled = false;
        // Screen-space camera canvases follow this camera. Keep ordinary scene geometry out of the capture.
        holder.transform.position = new Vector3(0, -50000f, 0);
        captureCamera.orthographic = true;
        captureCamera.nearClipPlane = 0.05f;
        captureCamera.farClipPlane = 2f;
        captureCamera.clearFlags = CameraClearFlags.SolidColor;
        captureCamera.backgroundColor = Color.clear;
        captureCamera.allowHDR = false;
        captureCamera.allowMSAA = false;
        surface = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        surface.name = "CairnVR.UI";
        surface.Create();
        captureCamera.targetTexture = surface;
    }

    private void CaptureCanvas()
    {
        EnsureCamera();
        if (Time.unscaledTime >= nextDiscovery)
        {
            canvases = Object.FindObjectsOfType<Canvas>(true).Where(c => c.isRootCanvas).ToArray();
            nextDiscovery = Time.unscaledTime + 0.5f;
        }
        redirected.Clear();
        var mask = 0;
        try
        {
            foreach (var c in canvases)
            {
                if (c == null || !c.isActiveAndEnabled || !c.isRootCanvas || c.targetDisplay != 0) continue;
                var mode = c.renderMode;
                if (mode == RenderMode.WorldSpace) continue;
                var previousCamera = c.worldCamera;
                if (mode == RenderMode.ScreenSpaceCamera && previousCamera != null && previousCamera.targetTexture != null) continue;
                redirected.Add((c, mode, previousCamera, c.planeDistance));
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = captureCamera;
                c.planeDistance = 1f;
                mask |= 1 << c.gameObject.layer;
            }
            if (lastCanvasCount != redirected.Count)
            {
                Log.LogInfo($"Stereo UI capture: {redirected.Count} visible root canvases.");
                lastCanvasCount = redirected.Count;
            }
            if (redirected.Count == 0) return;
            captureCamera.cullingMask = mask;
            Canvas.ForceUpdateCanvases();
            captureCamera.Render();
        }
        finally
        {
            foreach (var (canvas, mode, camera, distance) in redirected)
            {
                if (canvas == null) continue;
                canvas.worldCamera = camera;
                canvas.planeDistance = distance;
                canvas.renderMode = mode;
            }
        }
    }

    private void Composite(Camera eye)
    {
        if (eye == null || eye.targetTexture == null) return;
        if (capturedFrame != Time.frameCount)
        {
            capturedFrame = Time.frameCount;
            CaptureCanvas();
        }
        if (lastCanvasCount > 0) DrawPanel(eye, trackedHeadPose());
        CaptureDiagnostic(eye);
    }

    private Material panelMaterial;
    private void DrawPanel(Camera eye, Pose head)
    {
        if (panelMaterial == null)
        {
            var shader = Shader.Find("UI/Default");
            if (shader == null) return;
            panelMaterial = new Material(shader);
            panelMaterial.mainTexture = surface;
        }
        // Express the shared head plane in eye space, retaining IPD, cant and optical offsets.
        // Both source poses are tracking-local even when the game camera moves around the mountain.
        var toEye = Quaternion.Inverse(eye.transform.localRotation);
        var headToEye = Matrix4x4.TRS(toEye * (head.position - eye.transform.localPosition),
            toEye * head.rotation, Vector3.one);
        var projection = eye.projectionMatrix;
        // Keep screen-space menus visible over the scene. Preserve clip W for perspective-correct UVs.
        projection.SetRow(2, projection.GetRow(3) * -.9999f);
        var previous = RenderTexture.active;
        RenderTexture.active = eye.targetTexture;
        GL.PushMatrix();
        try
        {
            if (!panelMaterial.SetPass(0)) return;
            GL.modelview = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * headToEye;
            GL.LoadProjectionMatrix(projection);
            GL.Begin(GL.QUADS);
            GL.Color(Color.white);
            Vertex(-HalfWidth, -HalfHeight, 0, 0);
            Vertex(-HalfWidth, HalfHeight, 0, 1);
            Vertex(HalfWidth, HalfHeight, 1, 1);
            Vertex(HalfWidth, -HalfHeight, 1, 0);
            GL.End();
        }
        finally
        {
            GL.PopMatrix();
            RenderTexture.active = previous;
        }
    }

    private static void Vertex(float x, float y, float u, float v)
    {
        GL.TexCoord2(u, v);
        GL.Vertex3(x, y, PanelDistance);
    }

    private void CaptureDiagnostic(Camera eye)
    {
        if (string.IsNullOrEmpty(captureFolder) || captures >= 2 || Time.realtimeSinceStartup < 14) return;
        var previous = RenderTexture.active;
        var rt = eye.targetTexture;
        var image = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        try
        {
            RenderTexture.active = rt;
            image.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            image.Apply();
            Directory.CreateDirectory(captureFolder);
            File.WriteAllBytes(Path.Combine(captureFolder, eye.name + ".png"), ImageConversion.EncodeToPNG(image));
            captures++;
        }
        finally
        {
            Object.Destroy(image);
            RenderTexture.active = previous;
        }
    }
}
