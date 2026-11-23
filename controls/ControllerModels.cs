using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace CairnVR.Controls;

public static class ControllerModels
{
    static readonly Hand[] hands = new Hand[2];
    static readonly FrameModel[] frames = new FrameModel[2];
    static readonly bool[] attempted = new bool[2];
    static readonly Dictionary<string, Material> faceCaps = new();
    static ControllerIcons icons;
    static Quaternion iconFacing;
    static EventInfo beforeEye, afterEye;
    public static event Action<Camera> AfterControllersRendered;
    static CommandBuffer drawCommands;
    static Camera attachedEye;
    static bool collecting;
    static string modelDirectory;
    static Mesh sphere;
    static Material shell, cap, depthOnly;
    static bool disabled, leftReaches;
    static int lastFrame = -1;

    // Anchors are in the same tracking origin as the eyes, even when recentered.
    // A missing pose or a button absent from the current profile returns null.
    public static Transform GetButtonAnchor(string control)
    {
        foreach (var hand in hands)
            if (hand != null && hand.Tracked && hand.Root && hand.Anchors.TryGetValue(control, out var anchor))
                return anchor;
        return null;
    }

    public static string ButtonForBinding(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int separator = path.IndexOf(">/", StringComparison.Ordinal);
        if (separator < 0 || !path.StartsWith("<", StringComparison.Ordinal)) return null;
        string layout = path.Substring(1, separator - 1);
        if (!(layout.Equals("Gamepad", StringComparison.OrdinalIgnoreCase) || layout.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)
            || layout.StartsWith("XInput", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("DualShock", StringComparison.OrdinalIgnoreCase)
            || layout.StartsWith("DualSense", StringComparison.OrdinalIgnoreCase))) return null;
        string component = path.Substring(separator + 2).ToLowerInvariant();
        if (component.StartsWith("leftstick/", StringComparison.Ordinal)) return "LeftStick";
        if (component.StartsWith("rightstick/", StringComparison.Ordinal)) return "RightStick";
        return component switch
        {
            "buttonsouth" or "a" => "A", "buttonwest" or "x" => "X",
            "buttoneast" or "b" => "B", "buttonnorth" or "y" => "Y",
            "dpad/up" => "Up", "dpad/down" => "Down", "dpad/left" => "Left", "dpad/right" => "Right",
            "leftshoulder" => "LB", "rightshoulder" => "RB", "lefttrigger" => "LT", "righttrigger" => "RT",
            "leftstick" => "LeftStick", "rightstick" => "RightStick", "leftstickpress" => "LeftStickPress", "rightstickpress" => "RightStickPress",
            "start" or "startbutton" => "Menu", "select" or "selectbutton" => "View", _ => null
        };
    }

    public static bool GazeAvailable => ControlsPlugin.Gaze && Tracking.TryRead(out var s) && (s.Valid & 1u) != 0;

    internal static void Start()
    {
        icons = new ControllerIcons();
        modelDirectory = FrameModel.FindModels();
        leftReaches = Environment.GetEnvironmentVariable("CAIRNVR_LEFTHANDED") == "1";
        var rig = Type.GetType("CairnVR.CameraRig.StereoView, CairnVR.Camera", true);
        beforeEye = rig.GetEvent("BeforeEyeRender");
        afterEye = rig.GetEvent("AfterEyeRender");
        beforeEye.AddEventHandler(null, (Action<Camera>)Track);
        afterEye.AddEventHandler(null, (Action<Camera>)Detach);
    }

    static Material Surface(Color color, Texture texture = null)
    {
        var shader = Shader.Find(texture ? "Unlit/Texture" : "Unlit/Color");
        if (!shader) shader = Shader.Find("Sprites/Default");
        if (!shader) throw new InvalidOperationException("No controller material shader is available.");
        var mat = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (mat.HasProperty("_Color")) mat.color = color;
        if (texture) mat.mainTexture = texture;
        return mat;
    }

    static void Track(Camera eye)
    {
        if (disabled || !eye || !eye.transform.parent) return;
        try
        {
            if (lastFrame != Time.frameCount)
            {
                lastFrame = Time.frameCount;
                bool valid = Tracking.TryRead(out var sample);
                for (int i = 0; i < hands.Length; i++)
                {
                    bool dominant = (i == 0) == leftReaches;
                    bool tracked = valid && (sample.Valid & (dominant ? 2u : 16u)) != 0;
                    var hand = hands[i];
                    if (!tracked)
                    {
                        if (hand != null) hand.Tracked = false;
                        continue;
                    }
                    bool frame = (sample.FrameHands & (1u << i)) != 0;
                    if (hand == null || !hand.Root || hand.Root.parent != eye.transform.parent || hand.Frame != frame)
                    {
                        hand?.Dispose();
                        hands[i] = hand = new Hand(i, frame, eye.transform.parent);
                    }
                    var pose = dominant ? sample.Grip : sample.OtherGrip;
                    hand.Root.SetLocalPositionAndRotation(pose.Position, pose.Rotation);
                    hand.Tracked = true;
                }
                if (hands[0]?.Tracked == true || hands[1]?.Tracked == true)
                {
                    // Fix the layout for the stereo pair, including canted eye displays.
                    iconFacing = eye.transform.rotation;
                    icons.Refresh();
                }
            }
            QueueDraws(eye);
        }
        catch (Exception e)
        {
            disabled = true;
            foreach (var h in hands) if (h != null) h.Tracked = false;
            ControlsPlugin.Logger.LogError($"Controller rendering stopped: {e}");
        }
    }

    // This executes as part of the eye camera's own render. Calling DrawMeshNow
    // after Camera.Render has returned can pick up the desktop camera's matrices.
    static void QueueDraws(Camera eye)
    {
        Detach(null);
        if (hands[0]?.Tracked != true && hands[1]?.Tracked != true) return;
        drawCommands ??= new CommandBuffer { name = "CAIRN tracked controllers and tutorial" };
        drawCommands.Clear();
        drawCommands.SetRenderTarget(new RenderTargetIdentifier(eye.targetTexture));
        drawCommands.ClearRenderTarget(true, false, Color.clear);
        collecting = true;
        try
        {
            foreach (var hand in hands)
                if (hand != null && hand.Tracked && hand.Root) hand.Draw(depthPass: true);
            foreach (var hand in hands)
                if (hand != null && hand.Tracked && hand.Root) hand.Draw(depthPass: false);
            foreach (var hand in hands)
                if (hand != null && hand.Tracked && hand.Root)
                    icons.Draw(hand.Anchors, hand.Root, hand.Frame, hand.PadCenter, iconFacing);
            AfterControllersRendered?.Invoke(eye);
        }
        finally { collecting = false; }
        eye.AddCommandBuffer(CameraEvent.AfterEverything, drawCommands);
        attachedEye = eye;
    }

    public static void SubmitMesh(Mesh mesh, Matrix4x4 modelToWorld, Material material, int submesh = 0)
    {
        if (collecting && mesh && material)
            drawCommands.DrawMesh(mesh, modelToWorld, material, submesh, 0);
    }

    static void Detach(Camera unused)
    {
        if (attachedEye) attachedEye.RemoveCommandBuffer(CameraEvent.AfterEverything, drawCommands);
        attachedEye = null;
    }

    static void EnsureFallback()
    {
        if (sphere) return;
        var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
        sphere.hideFlags = HideFlags.DontUnloadUnusedAsset;
        primitive.SetActive(false);
        Object.Destroy(primitive);
        shell = Surface(new Color(.18f, .20f, .19f));
        cap = Surface(new Color(.60f, .56f, .47f));
    }

    static Material DepthMaterial()
    {
        if (depthOnly) return depthOnly;
        // The game's stripped player supplies Sprites/Default for our textured
        // pass. Its ZWrite Off would paint hidden parts on top of the grip.
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (!shader) shader = Shader.Find("Standard");
        if (!shader) return null;
        depthOnly = new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        if (depthOnly.HasProperty("_SrcBlend")) depthOnly.SetInt("_SrcBlend", (int)BlendMode.Zero);
        if (depthOnly.HasProperty("_DstBlend")) depthOnly.SetInt("_DstBlend", (int)BlendMode.One);
        if (depthOnly.HasProperty("_ZWrite")) depthOnly.SetInt("_ZWrite", 1);
        if (depthOnly.HasProperty("_ZTest")) depthOnly.SetInt("_ZTest", (int)CompareFunction.LessEqual);
        if (depthOnly.HasProperty("_Cull")) depthOnly.SetInt("_Cull", (int)CullMode.Off);
        return depthOnly;
    }

    sealed class Hand
    {
        internal readonly Transform Root;
        internal readonly Dictionary<string, Transform> Anchors = new();
        internal readonly bool Frame;
        internal readonly Vector3 PadCenter;
        internal bool Tracked;
        readonly List<(Mesh mesh, Matrix4x4 local, Material mat)> parts = new();
        readonly bool right;
        Material textured;

        internal Hand(int index, bool frame, Transform rig)
        {
            right = index == 1;
            Frame = frame;
            Root = new GameObject(right ? "CairnVR.RightController" : "CairnVR.LeftController").transform;
            Root.SetParent(rig, false);
            if (frame && !attempted[index] && modelDirectory != null)
            {
                attempted[index] = true;
                try { frames[index] = FrameModel.Load(modelDirectory, right); }
                catch (Exception e) { ControlsPlugin.Logger.LogWarning($"Using a simplified controller: {e.Message}"); }
            }
            if (frame && frames[index] is FrameModel model)
            {
                textured = Surface(Color.white, model.Atlas);
                var matrix = Matrix4x4.TRS(model.ModelOffset, model.ModelRotation, Vector3.one);
                parts.Add((model.Mesh, matrix, textured));
                foreach (var a in model.Anchors) AddAnchors(a.Key, matrix.MultiplyPoint3x4(a.Value));
                ControlsPlugin.Logger.LogInfo($"Loaded the Steam Frame {(right ? "right" : "left")} controller and its button anchors.");
            }
            else BuildFallback();
            var center = Vector3.zero;
            int count = 0;
            foreach (var anchor in Anchors)
                if (ControllerIcons.PadOffset(anchor.Key, frame, out _))
                {
                    center += anchor.Value.localPosition;
                    count++;
                }
            if (count > 0) PadCenter = center / count;
        }

        void Anchor(string name, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(Root, false);
            t.localPosition = pos;
            Anchors[name] = t;
        }

        void AddAnchors(string part, Vector3 pos)
        {
            string prefix = right ? "Right" : "Left";
            switch (part)
            {
                case "button_a": Anchor("A", pos); break;
                case "button_b": Anchor("B", pos); break;
                case "button_x": Anchor("X", pos); break;
                case "button_y": Anchor("Y", pos); break;
                case "thumbstick": Anchor(prefix + "Stick", pos); Anchor(prefix + "StickPress", pos); break;
                case "trigger": Anchor(right ? "RT" : "LT", pos); break;
                case "bumper": Anchor(right ? "RB" : "LB", pos); break;
                case "button_grip": Anchor(prefix + "Grip", pos); break;
                case "button_view": Anchor("View", pos); break;
                case "button_menu": Anchor("Menu", pos); break;
                case "dpad":
                    Anchor("Up", pos + new Vector3(0, -.003f, .005f));
                    Anchor("Down", pos + new Vector3(0, .003f, -.005f));
                    Anchor("Left", pos + Vector3.left * .006f);
                    Anchor("Right", pos + Vector3.right * .006f);
                    break;
            }
        }

        void BuildFallback()
        {
            EnsureFallback();
            float sign = right ? 1 : -1;
            void Piece(Vector3 pos, Vector3 size, Material material, float tilt = 0)
                => parts.Add((sphere, Matrix4x4.TRS(pos, Quaternion.Euler(tilt, 0, 0), size), material));
            void Button(string part, Vector3 pos, Vector3 size)
            {
                var material = cap;
                var paint = ControllerIcons.FacePaint(part);
                if (paint.a != 0 && !faceCaps.TryGetValue(part, out material))
                    faceCaps[part] = material = Surface(paint);
                Piece(pos, size, material);
                AddAnchors(part, pos);
            }
            Piece(new Vector3(0, -.012f, -.018f), new Vector3(.039f, .05f, .115f), shell, -18);
            Piece(new Vector3(0, .004f, .038f), new Vector3(.067f, .032f, .068f), shell);
            Button("thumbstick", new Vector3(-sign * .015f, .022f, .035f), new Vector3(.018f, .009f, .018f));
            Button("trigger", new Vector3(0, -.019f, .065f), new Vector3(.023f, .022f, .018f));
            Button("button_grip", new Vector3(-sign * .019f, -.005f, -.002f), new Vector3(.007f, .015f, .034f));
            if (Frame) Button("bumper", new Vector3(0, .004f, .067f), new Vector3(.033f, .010f, .012f));
            Button(right && Frame ? "button_menu" : right ? "system" : Frame ? "button_view" : "button_menu",
                new Vector3(0, .019f, .016f), new Vector3(.008f, .004f, .008f));
            if (Frame && !right)
                Button("dpad", new Vector3(-.012f, .020f, .051f), new Vector3(.021f, .006f, .021f));
            else
            {
                var names = Frame ? new[] { "button_a", "button_b", "button_y", "button_x" }
                    : right ? new[] { "button_a", "button_b" } : new[] { "button_x", "button_y" };
                var offsets = new[] { new Vector2(0, -.010f), new Vector2(.010f, 0), new Vector2(0, .010f), new Vector2(-.010f, 0) };
                for (int i = 0; i < names.Length; i++)
                    Button(names[i], new Vector3(sign * .013f + offsets[i].x, .020f, .050f + offsets[i].y),
                        new Vector3(.009f, .005f, .009f));
            }
        }

        internal void Draw(bool depthPass)
        {
            var world = Root.localToWorldMatrix;
            foreach (var part in parts)
            {
                if (depthPass && part.mat.renderQueue < (int)RenderQueue.Transparent) continue;
                SubmitMesh(part.mesh, world * part.local, depthPass ? DepthMaterial() : part.mat);
            }
        }

        internal void Dispose()
        {
            if (Root) Object.Destroy(Root.gameObject);
            if (textured) Object.Destroy(textured);
        }
    }

    internal static void Stop()
    {
        beforeEye?.RemoveEventHandler(null, (Action<Camera>)Track);
        afterEye?.RemoveEventHandler(null, (Action<Camera>)Detach);
        Detach(null);
        drawCommands?.Release();
        icons?.Dispose();
        for (int i = 0; i < hands.Length; i++)
        {
            hands[i]?.Dispose(); hands[i] = null;
            frames[i]?.Dispose(); frames[i] = null;
        }
        if (shell) Object.Destroy(shell);
        if (cap) Object.Destroy(cap);
        foreach (var material in faceCaps.Values) if (material) Object.Destroy(material);
        faceCaps.Clear();
        if (depthOnly) Object.Destroy(depthOnly);
        if (sphere) Object.Destroy(sphere);
    }
}
