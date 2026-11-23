using BepInEx;
using BepInEx.Unity.IL2CPP;
using CairnVR.Controls;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnVR.Tutorial;

[BepInPlugin("cairn.vr.tutorial", "CAIRN VR tutorial", "1.0.0")]
[BepInDependency("community.cairnvr.camera")]
[BepInDependency("cairn.vr.controls")]
public sealed class TutorialPlugin : BasePlugin
{
    readonly TutorialHints hints = new();
    readonly Callout[] callouts = new Callout[TutorialHints.Capacity];
    float nextPoll, nextErrorLog;
    int lastFrame = -1;
    Camera referenceEye;
    Quaternion billboard;
    Material ruleMaterial;
    Mesh rule, ring;
    TMP_Text style;

    public override void Load()
    {
        if (Environment.GetEnvironmentVariable("CAIRN_VR") != "1") return;
        ControllerModels.AfterControllersRendered += Draw;
        Log.LogInfo("Listening for CAIRN's active tutorial instructions.");
    }

    void Draw(Camera eye)
    {
        // A failed game UI lookup must never interrupt the eye camera or the game's tutorial.
        try
        {
            if (lastFrame != Time.frameCount)
            {
                lastFrame = Time.frameCount;
                billboard = eye.transform.rotation;
                referenceEye = eye;
                if (Time.unscaledTime >= nextPoll)
                {
                    nextPoll = Time.unscaledTime + .12f;
                    hints.Refresh();
                    if (hints.Active.Count != 0 && !style) style = FindTutorialStyle();
                }
            }
            if (hints.Active.Count == 0) return;
            var left = ControllerModels.GetButtonAnchor("LeftStick");
            var right = ControllerModels.GetButtonAnchor("RightStick");
            int leftRow = 0, rightRow = 0;
            for (int i = 0; i < hints.Active.Count; ++i)
            {
                var hint = hints.Active[i];
                var anchor = ControllerModels.GetButtonAnchor(hint.Control);
                if (!anchor) continue;
                // Component anchors on a hand share a model parent even when the hands cross.
                bool onLeft = left && anchor.parent == left.parent;
                int row = onLeft ? leftRow++ : rightRow++;
                var hand = (onLeft ? left : right) ?? anchor;
                if (!style && callouts[i] == null) style = FindTutorialStyle();
                var callout = callouts[i] ??= new Callout(style);
                callout.Set(hint.Text, style);
                float side = onLeft ? -1 : 1;
                // Leave the space above the pad for its quick-inventory and face
                // icons. Low-held controllers can put a lower row outside the eye.
                float rowOffset = row * .047f;
                var rowBase = hand.position + billboard * new Vector3(0, -.024f - rowOffset, -.014f);
                if (referenceEye.WorldToViewportPoint(rowBase).y < .055f)
                    rowBase = hand.position + billboard * new Vector3(0, .082f + rowOffset, -.014f);
                var rightAxis = billboard * Vector3.right;
                // Keep the entire label in view. Use one reference eye for both images so a
                // label at the edge cannot flip to a different side in each eye.
                const float sideGap = .065f;
                var farEdge = referenceEye.WorldToViewportPoint(rowBase + rightAxis * (side * (sideGap + .003f + callout.Width)));
                if (farEdge.x < .055f || farEdge.x > .945f) side = -side;
                var tip = rowBase + rightAxis * (sideGap * side);
                Leader(anchor.position, tip);
                callout.Draw(tip + rightAxis * (.003f * side), billboard, side < 0);
            }
        }
        catch (Exception e)
        {
            hints.Active.Clear();
            if (Time.unscaledTime >= nextErrorLog)
            {
                nextErrorLog = Time.unscaledTime + 15f;
                Log.LogWarning($"VR tutorial callouts: {e}");
            }
        }
    }

    static TMP_Text FindTutorialStyle()
    {
        var hud = Object.FindObjectOfType<Cairn.UI.PopupTutorialHud>(true);
        if (hud && hud.inputInstructionPrefab && hud.inputInstructionPrefab.text && hud.inputInstructionPrefab.text.text && hud.inputInstructionPrefab.text.text.font)
            return hud.inputInstructionPrefab.text.text;
        var tip = Object.FindObjectOfType<Cairn.UI.TipTutorialHud>(true);
        if (tip && tip.text && tip.text.text && tip.text.text.font) return tip.text.text;
        // The game ships its typefaces through addressables; TMP_Settings.defaultFontAsset
        // is empty in the Windows build. The title, menu and climbing HUD use TMP UGUI.
        foreach (var label in Object.FindObjectsOfType<TextMeshProUGUI>())
            if (label && label.font && !string.IsNullOrWhiteSpace(label.text)) return label;
        return null;
    }

    void Leader(Vector3 from, Vector3 to)
    {
        if (!rule)
        {
            rule = ColoredShape("CAIRN tutorial leader", new Vector3[] { new(0, -.5f, 0), new(0, .5f, 0), new(1, .5f, 0), new(1, -.5f, 0) },
                                new[] { 0, 1, 2, 0, 2, 3 });
            const int sides = 18;
            var vertices = new Vector3[sides * 2];
            var faces = new int[sides * 6];
            for (int n = 0; n < sides; ++n)
            {
                float angle = n * Mathf.PI * 2f / sides;
                var radial = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
                int a = n * 2, b = (a + 2) % vertices.Length, i = n * 6;
                vertices[a] = radial; vertices[a + 1] = radial * .68f;
                faces[i] = a; faces[i+1] = a+1; faces[i+2] = b;
                faces[i+3] = b; faces[i+4] = a+1; faces[i+5] = b+1;
            }
            ring = ColoredShape("CAIRN tutorial button ring", vertices, faces);
            var shader = Shader.Find("UI/Default");
            if (shader) ruleMaterial = new Material(shader)
            {
                color = new Color(.96f, .91f, .76f), mainTexture = Texture2D.whiteTexture, hideFlags = HideFlags.DontUnloadUnusedAsset
            };
        }
        if (!ruleMaterial) return;
        from -= billboard * Vector3.forward * .003f;
        ControllerModels.SubmitMesh(ring, Matrix4x4.TRS(from, billboard, Vector3.one * .0055f), ruleMaterial, 0);
        Vector3 direction = to - from;
        if (direction.sqrMagnitude < .000001f) return;
        var up = Vector3.Cross(billboard * Vector3.forward, direction.normalized);
        if (up.sqrMagnitude < .001f) return;
        var facing = Quaternion.LookRotation(Vector3.Cross(direction, up), up);
        var matrix = Matrix4x4.TRS(from, facing, new Vector3(direction.magnitude, .0014f, 1));
        ControllerModels.SubmitMesh(rule, matrix, ruleMaterial, 0);
    }

    static Mesh ColoredShape(string name, Vector3[] vertices, int[] triangles)
    {
        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
        mesh.vertices = vertices;
        mesh.colors32 = Enumerable.Repeat(new Color32(255, 255, 255, 255), vertices.Length).ToArray();
        mesh.uv = new Vector2[vertices.Length];
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    public override bool Unload()
    {
        ControllerModels.AfterControllersRendered -= Draw;
        foreach (var item in callouts) item?.Dispose();
        if (ruleMaterial) Object.Destroy(ruleMaterial);
        if (rule) Object.Destroy(rule);
        if (ring) Object.Destroy(ring);
        return true;
    }
}

internal sealed class Callout
{
    readonly GameObject holder;
    readonly TextMeshPro text;
    TMP_FontAsset font;
    string caption;
    Bounds bounds;
    float scale;
    Material shadow, shadowSource;
    public float Width => Mathf.Max(0, bounds.size.x * scale);

    public Callout(TMP_Text style)
    {
        holder = new GameObject("CAIRN VR tutorial lettering");
        holder.SetActive(false);
        Object.DontDestroyOnLoad(holder);
        // TMP lays out the glyphs here. Only explicit, per-eye draws display the mesh.
        holder.transform.position = new Vector3(0, -50000, 0);
        text = holder.AddComponent<TextMeshPro>();
        if (style && style.font) text.font = style.font;
        text.fontSize = 3;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.richText = false;
        text.rectTransform.sizeDelta = new Vector2(20, 6);
        holder.SetActive(true);
        holder.GetComponent<MeshRenderer>().enabled = false;
    }

    public void Set(string value, TMP_Text style)
    {
        var currentFont = style && style.font ? style.font : font;
        if (!currentFont)
        {
            foreach (var loaded in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (loaded && loaded.material && loaded.HasCharacter('X')) { currentFont = loaded; break; }
        }
        if (!currentFont) return;
        if (caption == value && font == currentFont) return;
        caption = value;
        font = currentFont;
        if (font) text.font = font;
        if (style) text.fontStyle = style.fontStyle;
        // Native menu entries can be black on an opaque panel. Floating over open rock,
        // use the game's pale lettering when the sampled style expects a light backing.
        var paper = new Color(.97f, .94f, .86f);
        var color = style ? style.color : paper;
        if (color.grayscale < .72f) color = paper;
        color.a = 1;
        text.color = color;
        text.text = value;
        text.ForceMeshUpdate(true, true);
        // Fallback glyphs can create additional renderers even on a disabled main renderer.
        foreach (var renderer in holder.GetComponentsInChildren<MeshRenderer>(true)) renderer.enabled = false;
        bounds = text.textBounds;
        float targetHeight = value.Contains('\n') ? .032f : .016f;
        scale = Mathf.Min(targetHeight / Mathf.Max(.001f, bounds.size.y), .22f / Mathf.Max(.001f, bounds.size.x));
    }

    public void Draw(Vector3 point, Quaternion face, bool alignRight)
    {
        if (!font || scale <= 0) return;
        float edge = alignRight ? bounds.max.x : bounds.min.x;
        var origin = point - face * new Vector3(edge * scale, bounds.center.y * scale, 0);
        var matrix = Matrix4x4.TRS(origin, face, Vector3.one * scale);
        var info = text.textInfo;
        var meshes = info.meshInfo;
        for (int i = 0; i < info.materialCount; ++i)
        {
            var part = meshes[i];
            if (part.vertexCount <= 0 || !part.mesh || !part.material) continue;
            if (i == 0 && part.material.HasProperty("_FaceColor"))
            {
                if (shadowSource != part.material)
                {
                    if (shadow) Object.Destroy(shadow);
                    shadowSource = part.material;
                    shadow = new Material(part.material) { hideFlags = HideFlags.DontUnloadUnusedAsset };
                    shadow.SetColor("_FaceColor", new Color(.08f, .065f, .055f, .83f));
                }
                var offset = face * new Vector3(.0011f, -.0013f, .0005f);
                ControllerModels.SubmitMesh(part.mesh, Matrix4x4.Translate(offset) * matrix, shadow, 0);
            }
            ControllerModels.SubmitMesh(part.mesh, matrix, part.material, 0);
        }
    }

    public void Dispose()
    {
        if (shadow) Object.Destroy(shadow);
        Object.Destroy(holder);
    }
}
