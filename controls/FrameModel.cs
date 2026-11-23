using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnVR.Controls;

// Load the player's SteamVR meshes in model space. The component origins in
// Valve's manifest are attachment points; the OBJ vertices are already posed.
internal sealed class FrameModel
{
    internal Mesh Mesh;
    internal Texture2D Atlas;
    internal readonly Dictionary<string, Vector3> Anchors = new();
    internal Quaternion ModelRotation;
    internal Vector3 ModelOffset;

    internal static string FindModels()
    {
        var runtimes = new List<string>();
        var registry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "openvr", "openvrpaths.vrpath");
        if (File.Exists(registry))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(registry));
                foreach (var path in json.RootElement.GetProperty("runtime").EnumerateArray())
                    runtimes.Add(path.GetString());
            }
            catch (Exception e) { ControlsPlugin.Logger.LogDebug(e.Message); }
        }
        runtimes.Add(Path.GetFullPath(Path.Combine(Paths.GameRootPath, "..", "SteamVR")));
        foreach (var runtime in runtimes.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var folder = Path.Combine(runtime, "drivers", "frame_controller", "resources", "rendermodels");
            if (Directory.Exists(Path.Combine(folder, "frame_controller_right"))) return folder;
        }
        return null;
    }

    static Vector3 Position(JsonElement coordinates) =>
        new(coordinates[0].GetSingle(), coordinates[1].GetSingle(), -coordinates[2].GetSingle());

    internal static FrameModel Load(string root, bool right)
    {
        var name = "frame_controller_" + (right ? "right" : "left");
        var dir = Path.Combine(root, name);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, name + ".json")));
        var components = manifest.RootElement.GetProperty("components");
        var grip = components.GetProperty("openxr_grip").GetProperty("component_local");
        var r = grip.GetProperty("rotate_xyz");
        var gripRotation = Quaternion.AngleAxis(r[2].GetSingle(), Vector3.forward)
            * Quaternion.AngleAxis(-r[1].GetSingle(), Vector3.up) * Quaternion.AngleAxis(-r[0].GetSingle(), Vector3.right);
        var model = new FrameModel { ModelRotation = Quaternion.Inverse(gripRotation) };
        model.ModelOffset = model.ModelRotation * -Position(grip.GetProperty("origin"));
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();
        var surfaces = new List<(int end, Color32 paint)>();
        try
        {
            foreach (var part in components.EnumerateObject())
            {
                if (!part.Value.TryGetProperty("filename", out var path) || part.Name == "status") continue;
                string filename = path.GetString();
                if (string.IsNullOrEmpty(filename) || Path.GetFileName(filename) != filename)
                    throw new InvalidDataException("Unexpected SteamVR render-model path.");
                AppendObj(Path.Combine(dir, filename), vertices, normals, uvs, triangles);
                surfaces.Add((triangles.Count, ControllerIcons.FacePaint(part.Name)));
                if (part.Value.TryGetProperty("component_local", out var local))
                    model.Anchors[part.Name] = Position(local.GetProperty("origin"));
            }
            model.Mesh = new Mesh { name = name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (vertices.Count > 65535) model.Mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            model.Mesh.vertices = vertices.ToArray();
            model.Mesh.normals = normals.ToArray();
            model.Mesh.uv = uvs.ToArray();
            model.Mesh.triangles = triangles.ToArray();
            model.Mesh.RecalculateBounds();
            model.Mesh.UploadMeshData(true);
            model.Atlas = ReadAtlas(Path.Combine(dir, name + "_color.png"), uvs, normals, triangles, surfaces);
            return model;
        }
        catch { model.Dispose(); throw; }
    }

    static Texture2D ReadAtlas(string path, List<Vector2> uv, List<Vector3> normals, List<int> triangles,
        List<(int end, Color32 paint)> surfaces)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, true);
        try
        {
            if (!ImageConversion.LoadImage(source, File.ReadAllBytes(path), false))
                throw new InvalidDataException("Cannot read the SteamVR controller texture.");
            // The stock atlas is almost black. Retain its printed glyphs and lift
            // the shell to charcoal so the controls remain legible at night.
            int mip = source.mipmapCount > 1 ? 1 : 0;
            Color32[] pixels = source.GetPixels32(mip).ToArray();
            int width = source.width >> mip, height = source.height >> mip;
            ShadeAtlas(pixels, width, height, uv, normals, triangles, surfaces);
            var atlas = new Texture2D(width, height, TextureFormat.RGBA32, true)
            { hideFlags = HideFlags.DontUnloadUnusedAsset };
            atlas.SetPixels32(pixels);
            atlas.Apply(true, true);
            return atlas;
        }
        finally { Object.Destroy(source); }
    }

    static Color32[] InkRamp(Color32 paint)
    {
        var colors = new Color32[256];
        for (int i = 0; i < colors.Length; i++)
        {
            float t = Mathf.Sqrt(i / 255f);
            colors[i] = Color32.Lerp(paint, new Color32(245, 234, 212, 255), t);
        }
        return colors;
    }

    static Color32 Shade(Color32 ink, int light) =>
        new((byte)(ink.r * light / 255), (byte)(ink.g * light / 255), (byte)(ink.b * light / 255), 255);

    // Bake the cap paint and matte lighting into the same atlas as the shell.
    // Sampling the original ink keeps Valve's printed letters on the buttons.
    static void ShadeAtlas(Color32[] pixels, int width, int height, List<Vector2> uv, List<Vector3> normals,
        List<int> faces, List<(int end, Color32 paint)> surfaces)
    {
        var shellInk = InkRamp(new Color32(29, 33, 34, 255));
        var tone = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; ++i)
        {
            var c = pixels[i];
            tone[i] = (byte)((c.r * 54 + c.g * 183 + c.b * 19) >> 8);
            pixels[i] = Shade(shellInk[tone[i]], 185);
        }
        var light = new float[normals.Count];
        for (int i = 0; i < light.Length; i++)
        {
            var n = normals[i];
            float dot = n.y * .82f - n.z * .43f - n.x * .38f;
            light[i] = 95 + 160 * Math.Clamp((dot + .40f) / 1.40f, 0f, 1f);
        }
        int first = 0;
        foreach (var surface in surfaces)
        {
            var ink = surface.paint.a == 0 ? shellInk : InkRamp(surface.paint);
            for (int f = first; f < surface.end; f += 3)
                Rasterize(f, ink);
            first = surface.end;
        }

        void Rasterize(int f, Color32[] ink)
        {
            int a = faces[f], b = faces[f + 1], c = faces[f + 2];
            float ax = uv[a].x * width, ay = uv[a].y * height;
            float bx = uv[b].x * width, by = uv[b].y * height;
            float cx = uv[c].x * width, cy = uv[c].y * height;
            float det = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (Math.Abs(det) < .0001f) return;
            int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(ay, Math.Min(by, cy))));
            int y1 = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + .5f - ax, dy = y + .5f - ay;
                    float wb = (dx * (cy - ay) - dy * (cx - ax)) / det;
                    float wc = ((bx - ax) * dy - (by - ay) * dx) / det;
                    float wa = 1 - wb - wc;
                    if (wa >= 0 && wb >= 0 && wc >= 0)
                    {
                        int pixel = y * width + x;
                        pixels[pixel] = Shade(ink[tone[pixel]], (int)(wa * light[a] + wb * light[b] + wc * light[c]));
                    }
                }
        }
    }

    static void AppendObj(string path, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uv, List<int> faces)
    {
        var pos = new List<Vector3>();
        var tex = new List<Vector2>();
        var norm = new List<Vector3>();
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        static float Number(string word) => float.Parse(word, CultureInfo.InvariantCulture);
        int VertexIndex(string key)
        {
            if (indices.TryGetValue(key, out int index)) return index;
            var fields = key.Split('/');
            index = vertices.Count;
            vertices.Add(pos[int.Parse(fields[0], CultureInfo.InvariantCulture) - 1]);
            uv.Add(tex[int.Parse(fields[1], CultureInfo.InvariantCulture) - 1]);
            normals.Add(norm[int.Parse(fields[2], CultureInfo.InvariantCulture) - 1]);
            indices.Add(key, index);
            return index;
        }
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length < 3 || (line[0] != 'v' && line[0] != 'f')) continue;
            var words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            switch (words[0])
            {
                case "v": pos.Add(new Vector3(Number(words[1]), Number(words[2]), -Number(words[3]))); break;
                case "vn": norm.Add(new Vector3(Number(words[1]), Number(words[2]), -Number(words[3]))); break;
                case "vt": tex.Add(new Vector2(Number(words[1]), Number(words[2]))); break;
                case "f":
                    int first = VertexIndex(words[1]), previous = VertexIndex(words[2]);
                    for (int i = 3; i < words.Length; i++)
                    {
                        int last = VertexIndex(words[i]);
                        faces.Add(first); faces.Add(last); faces.Add(previous);
                        previous = last;
                    }
                    break;
            }
        }
    }

    internal void Dispose()
    {
        if (Mesh) Object.Destroy(Mesh);
        if (Atlas) Object.Destroy(Atlas);
    }
}
