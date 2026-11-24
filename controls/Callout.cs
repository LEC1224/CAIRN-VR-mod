using System;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnVR.Controls;

public sealed class Callout
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

    public void Draw(Vector3 point, Quaternion face, bool alignRight, float magnification = 1f)
    {
        if (!font || scale <= 0) return;
        float edge = alignRight ? bounds.max.x : bounds.min.x;
        float size = scale * magnification;
        var origin = point - face * new Vector3(edge * size, bounds.center.y * size, 0);
        var matrix = Matrix4x4.TRS(origin, face, Vector3.one * size);
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
                var offset = face * new Vector3(.0011f, -.0013f, .0005f) * magnification;
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
