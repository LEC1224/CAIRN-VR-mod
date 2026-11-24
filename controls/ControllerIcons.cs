using System;
using System.Collections.Generic;
using TGBTools.InputImage;
using TheGameBakers.Cairn.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace CairnVR.Controls;

internal sealed class ControllerIcons
{
    const float HoverHeight = .010f;
    // Paint for the physical caps, sampled from CAIRN's light gamepad prompts.
    // The floating letters themselves come from the game's input image manager.
    static readonly (string control, string part, string path, Color32 paint)[] faceButtons =
    {
        ("A", "button_a", "/buttonSouth", new Color32(88, 176, 22, 255)),
        ("B", "button_b", "/buttonEast", new Color32(225, 69, 44, 255)),
        ("X", "button_x", "/buttonWest", new Color32(0, 101, 255, 255)),
        ("Y", "button_y", "/buttonNorth", new Color32(244, 189, 16, 255))
    };

    internal static Color32 FacePaint(string part)
    {
        foreach (var button in faceButtons)
            if (button.part == part) return button.paint;
        return default;
    }

    readonly Glyph[] letters = { new(), new(), new(), new() };
    readonly Glyph[] items = { new(), new(), new(), new() };
    readonly Dictionary<string, (Glyph art, bool backing)> visible = new();
    CrossMenuUI menu;
    float nextRefresh, nextMenuSearch, nextWarning;
    Shader shader;
    Glyph backing;

    internal void Refresh()
    {
        float now = Time.unscaledTime;
        if (now < nextRefresh) return;
        nextRefresh = now + .12f;
        visible.Clear();
        try
        {
            if (!shader) shader = Shader.Find("Sprites/Default");
            if (!shader) return;
            var images = InputImageManager.Instance;
            for (int i = 0; i < faceButtons.Length; i++)
            {
                var button = faceButtons[i];
                var glyph = letters[i];
                if (!glyph.Sprite && images != null && images.IsReady)
                    glyph.Set(images.GetSprite("Gamepad", "", button.path, default, default, false, false), shader);
                if (glyph.Sprite) visible[button.control] = (glyph, false);
            }

            var input = InputManager.IsReady ? InputManager.Instance : null;
            var map = input?.gameplayUIActionMap;
            if (!menu)
                foreach (var item in items) item.ReleaseSprite();
            if (map == null || !map.enabled) return;
            if ((!menu || !menu.isActiveAndEnabled) && now >= nextMenuSearch)
            {
                nextMenuSearch = now + 1f;
                menu = Object.FindObjectOfType<CrossMenuUI>();
            }
            if (!menu || !menu.isActiveAndEnabled) return;

            // The menu can fade out between presses. Its slots still contain the
            // current layer, including the item art for the player's equipment.
            ReadSlot(items[0], menu.up, map, "inventoryTop");
            ReadSlot(items[1], menu.right, map, "inventoryRight");
            ReadSlot(items[2], menu.down, map, "inventoryDown");
            ReadSlot(items[3], menu.left, map, "inventoryLeft");
        }
        catch (Exception e)
        {
            // A late-loading HUD or missing sprite must not stop controller tracking.
            if (now < nextWarning) return;
            nextWarning = now + 15f;
            ControlsPlugin.Logger.LogWarning($"Controller icons: {e.Message}");
        }
    }

    void ReadSlot(Glyph glyph, CrossMenuIconUI slot, InputActionMap map, string actionName)
    {
        if (!slot || !slot.isActiveAndEnabled) return;
        var action = map.FindAction(actionName, false);
        if (action == null || !action.enabled) return;
        var item = slot.itemRenderer;
        var image = slot.image;
        var sprite = item && item.isActiveAndEnabled ? item.Sprite
            : image && image.isActiveAndEnabled ? image.overrideSprite : null;
        if (!sprite) return;
        backing ??= Glyph.Disc(shader);
        glyph.Set(sprite, shader);
        glyph.Opacity = slot.group ? slot.group.alpha : 1f;
        var bindings = action.bindings;
        for (int i = 0; i < bindings.Count; i++)
        {
            var binding = bindings[i];
            if (binding.isPartOfComposite) continue;
            string control = ControllerModels.ButtonForBinding(binding.effectivePath);
            if (control != null) visible[control] = (glyph, true);
        }
    }

    internal void Draw(Dictionary<string, Transform> anchors)
    {
        foreach (var entry in visible)
        {
            if (!anchors.TryGetValue(entry.Key, out var anchor)) continue;
            var surface = anchor.localToWorldMatrix;
            var (glyph, drawBacking) = entry.Value;
            if (drawBacking)
            {
                // Adjacent arms are 11.3 mm apart on the D-pad.
                backing.Draw(surface, HoverHeight - .0003f, .0108f);
                glyph.Draw(surface, HoverHeight, .0095f);
            }
            else glyph.Draw(surface, HoverHeight, .014f);
        }
    }

    internal void Dispose()
    {
        foreach (var glyph in letters) glyph.Dispose();
        foreach (var glyph in items) glyph.Dispose();
        backing?.Dispose();
    }

    sealed class Glyph
    {
        internal Sprite Sprite { get; private set; }
        Mesh mesh;
        Material material;
        internal float Opacity { set => material.color = new Color(1, 1, 1, value); }

        internal void ReleaseSprite()
        {
            if (ReferenceEquals(Sprite, null)) return;
            Sprite = null;
            material.mainTexture = null;
        }

        internal void Set(Sprite sprite, Shader shader)
        {
            if (!sprite || Sprite == sprite) return;
            var points = sprite.vertices;
            var uv = sprite.uv;
            var faces = sprite.triangles;
            var vertices = new Vector3[points.Length];
            var colors = new Color32[points.Length];
            var triangles = new int[faces.Length];
            var bounds = sprite.bounds;
            float size = Mathf.Max(.0001f, Mathf.Max(bounds.size.x, bounds.size.y));
            for (int i = 0; i < vertices.Length; ++i)
                vertices[i] = ((Vector3)points[i] - bounds.center) / size;
            Array.Fill(colors, new Color32(255, 255, 255, 255));
            for (int i = 0; i < triangles.Length; ++i) triangles[i] = faces[i];
            SetMesh(sprite.name, shader, sprite.texture, vertices, uv, colors, triangles);
            Sprite = sprite;
        }

        // A thin pale rim and a charcoal backing keep both light and dark
        // inventory artwork readable against snow, bare rock and night skies.
        internal static Glyph Disc(Shader shader)
        {
            const int sides = 32;
            var points = new Vector3[sides * 2 + 1];
            var colors = new Color32[points.Length];
            var faces = new int[sides * 9];
            colors[0] = new Color32(42, 44, 40, 255);
            for (int i = 0; i < sides; ++i)
            {
                float angle = i * 2f * Mathf.PI / sides;
                var radius = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * .5f;
                int a = 1 + 2 * i, b = 1 + 2 * ((i + 1) % sides), f = 9 * i;
                points[a] = radius * .90f;
                points[a + 1] = radius;
                colors[a] = colors[0];
                colors[a + 1] = new Color32(236, 231, 210, 255);
                faces[f] = 0; faces[f+1] = a; faces[f+2] = b;
                faces[f+3] = a; faces[f+4] = a+1; faces[f+5] = b;
                faces[f+6] = b; faces[f+7] = a+1; faces[f+8] = b+1;
            }
            var disc = new Glyph();
            disc.SetMesh("Quick inventory icon backing", shader, Texture2D.whiteTexture,
                points, new Vector2[points.Length], colors, faces);
            return disc;
        }

        void SetMesh(string name, Shader shader, Texture texture, Vector3[] vertices, Vector2[] uv, Color32[] colors, int[] triangles)
        {
            mesh ??= new Mesh { hideFlags = HideFlags.DontUnloadUnusedAsset };
            material ??= new Material(shader) { hideFlags = HideFlags.DontUnloadUnusedAsset };
            mesh.Clear();
            mesh.name = name;
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors32 = colors;
            mesh.triangles = triangles;
            material.mainTexture = texture;
        }

        internal void Draw(Matrix4x4 surface, float height, float size) =>
            ControllerModels.SubmitMesh(mesh, surface * Matrix4x4.TRS(Vector3.back * height, Quaternion.identity, Vector3.one * size), material);

        internal void Dispose()
        {
            if (mesh) Object.Destroy(mesh);
            if (material) Object.Destroy(material);
        }
    }
}
