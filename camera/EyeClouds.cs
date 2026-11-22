using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CairnVR.CameraRig;

internal static class EyeClouds
{
    private const string DepthKeyword = "DEPTHATTEN";
    private const int Cells = 40;
    private static readonly Vector4 FullOpacity = new(-2f, -1f, 1e8f, 2e8f);
    private static int depthRange, intersectionFade, drawFrame = -1, preparedFrame = -1, lastEyeFrame = -3;
    private static CloudsSea cloud;
    private static Material sourceMaterial, desktopMaterial, eyeMaterial;
    private static Mesh sourceMesh, eyeMesh;
    private static Vector3[] vertices;
    private static Il2CppStructArray<Color32> colors;
    private static Matrix4x4 previousMatrix;
    private static Vector3 previousCenter;
    private static Vector4 previousRange;
    private static bool failed, reported;

    internal static void Install()
    {
        try
        {
            depthRange = Shader.PropertyToID("_DepthAttenParams");
            intersectionFade = Shader.PropertyToID("_InvFade");
            new Harmony("community.cairnvr.camera.clouds").Patch(
                AccessTools.Method(typeof(CloudsSea), nameof(CloudsSea.RenderPlane)),
                prefix: new HarmonyMethod(typeof(EyeClouds), nameof(BeginPlane)),
                finalizer: new HarmonyMethod(typeof(EyeClouds), nameof(EndPlane)));
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static void BeginPlane(CloudsSea __instance, out Material __state)
    {
        __state = null;
        if (failed || lastEyeFrame < Time.frameCount - 1 || __instance.identifier != CloudsSea.Identifier.Top) return;
        try
        {
            var material = __instance.renderMaterial;
            if (!material || !material.IsKeywordEnabled(DepthKeyword)) return;
            if (sourceMaterial != material)
            {
                ReleaseGeometry();
                sourceMaterial = material;
                if (material.shader.name != "TGB/Tessel/Clouds Plane Transparent") return;
                desktopMaterial = new Material(material) { name = "CairnVR.DesktopTopCloud", hideFlags = HideFlags.HideAndDontSave };
                eyeMaterial = new Material(material) { name = "CairnVR.TopCloud", hideFlags = HideFlags.HideAndDontSave };
            }
            if (!desktopMaterial) return;
            desktopMaterial.CopyPropertiesFromMaterial(material);
            // Assign a distinct copy only for CAIRN's queued upper-plane draw.
            // Other objects can continue to use the same authored material.
            __state = material;
            __instance.renderMaterial = desktopMaterial;
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static void EndPlane(CloudsSea __instance, Material __state, Exception __exception)
    {
        if (!__state) return;
        __instance.renderMaterial = __state;
        if (__exception == null)
        {
            cloud = __instance;
            drawFrame = Time.frameCount;
        }
    }

    internal static void Render(Camera eye, Camera source)
    {
        lastEyeFrame = Time.frameCount;
        Material original = null;
        float fade = 0;
        try
        {
            if (!failed && cloud && drawFrame == Time.frameCount && (eye.cullingMask & 1) != 0)
            {
                try
                {
                    if (Prepare(eye))
                    {
                        fade = desktopMaterial.GetFloat(intersectionFade);
                        // The game has already queued its regular draw for all cameras.
                        // Suppress its transparent pass only while rendering this eye.
                        original = desktopMaterial;
                        desktopMaterial.SetFloat(intersectionFade, 0f);
                        var block = cloud.mpb;
                        bool overridesRange = block.HasVector(depthRange);
                        var savedRange = overridesRange ? block.GetVector(depthRange) : default;
                        try
                        {
                            if (overridesRange) block.SetVector(depthRange, FullOpacity);
                            Graphics.DrawMesh(eyeMesh, cloud.infinitePlaneMatrix, eyeMaterial, 0, eye, 0, block, false);
                        }
                        finally
                        {
                            // DrawMesh copies the block for this submission.
                            if (overridesRange) block.SetVector(depthRange, savedRange);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (original) original.SetFloat(intersectionFade, fade);
                    original = null;
                    Fail(ex);
                }
            }
            EyeLighting.Render(eye, source);
        }
        finally
        {
            if (original) original.SetFloat(intersectionFade, fade);
        }
    }

    private static bool Prepare(Camera eye)
    {
        var material = cloud.renderMaterial;
        var block = cloud.mpb;
        if (!material || block == null || !material.IsKeywordEnabled(DepthKeyword) ||
            block.HasFloat(intersectionFade) || !CloudsGlobalSettings.IsReady) return false;
        var plane = CloudsGlobalSettings.Instance.planeMesh;
        if (!plane) return false;
        if (plane != sourceMesh)
        {
            if (eyeMesh) Object.Destroy(eyeMesh);
            eyeMesh = null;
            sourceMesh = plane;
            previousMatrix = default;
            preparedFrame = -1;
            if (!MakeGrid(plane)) return false;
        }
        if (!eyeMesh || !eyeMaterial) return false;

        if (preparedFrame != drawFrame)
        {
            // Retain CAIRN's native noise, lighting, fog, geometry displacement,
            // weather property block and soft intersections. Replace only its
            // vertex-opacity smoothsteps, whose input is the forward-axis depth.
            eyeMaterial.CopyPropertiesFromMaterial(material);
            // Disabling DEPTHATTEN selects a stripped shader variant with
            // different normal-noise/wind behavior in CAIRN's shipped build.
            eyeMaterial.SetVector(depthRange, FullOpacity);
            var range = block.HasVector(depthRange) ? block.GetVector(depthRange) : material.GetVector(depthRange);
            if (range.y <= range.x || range.w <= range.z) return false;
            var center = eye.transform.parent.TransformPoint(StereoView.TrackedHeadPose.position);
            SetRadialOpacity(center, cloud.infinitePlaneMatrix, range);
            preparedFrame = drawFrame;
        }
        if (!reported)
        {
            reported = true;
            CameraPlugin.Logger.LogInfo("Rendering the upper weather clouds with radial distance attenuation.");
        }
        return true;
    }

    private static bool MakeGrid(Mesh plane)
    {
        // CAIRN uses Unity's built-in 10x10 plane. Keep its corner attributes
        // and winding, adding four controls per original edge for the opacity.
        if (plane.name != "Plane" || !plane.isReadable || plane.vertexCount != 121 ||
            plane.subMeshCount != 1 || plane.GetIndexCount(0) != 600) return false;
        var points = plane.vertices;
        var sourceUv = plane.uv;
        var normal = plane.normals[0];
        var tangent = plane.tangents[0];
        int row = Cells + 1;
        vertices = new Vector3[row * row];
        var normals = new Vector3[vertices.Length];
        var tangents = new Vector4[vertices.Length];
        var uv = new Vector2[vertices.Length];
        var triangles = new int[Cells * Cells * 6];
        int ti = 0;
        bool rowFirst = Vector3.Dot(Vector3.Cross(points[110] - points[0], points[10] - points[0]), normal) > 0f;
        for (int z = 0; z <= Cells; ++z)
        {
            float v = (float)z / Cells;
            var left = Vector3.Lerp(points[0], points[110], v);
            var right = Vector3.Lerp(points[10], points[120], v);
            var uvLeft = Vector2.Lerp(sourceUv[0], sourceUv[110], v);
            var uvRight = Vector2.Lerp(sourceUv[10], sourceUv[120], v);
            for (int x = 0; x <= Cells; ++x)
            {
                int i = z * row + x;
                float u = (float)x / Cells;
                vertices[i] = Vector3.Lerp(left, right, u);
                normals[i] = normal;
                tangents[i] = tangent;
                uv[i] = Vector2.Lerp(uvLeft, uvRight, u);
                if (x == Cells || z == Cells) continue;
                int b = i + (rowFirst ? row : 1), c = i + (rowFirst ? 1 : row);
                triangles[ti++] = i; triangles[ti++] = b; triangles[ti++] = c;
                triangles[ti++] = b; triangles[ti++] = i + row + 1; triangles[ti++] = c;
            }
        }
        eyeMesh = new Mesh { name = "CairnVR.TopCloudPlane", hideFlags = HideFlags.HideAndDontSave };
        eyeMesh.MarkDynamic();
        eyeMesh.vertices = vertices;
        eyeMesh.normals = normals;
        eyeMesh.tangents = tangents;
        eyeMesh.uv = uv;
        eyeMesh.triangles = triangles;
        eyeMesh.bounds = plane.bounds;
        colors = new Il2CppStructArray<Color32>(vertices.Length);
        return true;
    }

    private static void SetRadialOpacity(Vector3 center, Matrix4x4 matrix, Vector4 range)
    {
        if (matrix == previousMatrix && range == previousRange && (center - previousCenter).sqrMagnitude < .25f) return;
        previousMatrix = matrix;
        previousRange = range;
        previousCenter = center;
        var output = colors.AsSpan();
        float xOffset = matrix.m03 - center.x, yOffset = matrix.m13 - center.y, zOffset = matrix.m23 - center.z;
        for (int i = 0; i < vertices.Length; ++i)
        {
            var point = vertices[i];
            float x = matrix.m00 * point.x + matrix.m02 * point.z + xOffset;
            float y = matrix.m10 * point.x + matrix.m12 * point.z + yOffset;
            float z = matrix.m20 * point.x + matrix.m22 * point.z + zOffset;
            float distance = MathF.Sqrt(x * x + y * y + z * z);
            float alpha = Smoothstep(range.x, range.y, distance) * (1f - Smoothstep(range.z, range.w, distance));
            output[i] = new Color32(255, 255, 255, (byte)MathF.Round(alpha * 255f));
        }
        eyeMesh.colors32 = colors;
    }

    private static float Smoothstep(float start, float end, float distance)
    {
        float t = Math.Clamp((distance - start) / (end - start), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static void Fail(Exception ex)
    {
        failed = true;
        CameraPlugin.Logger.LogWarning($"Using the game's cloud shading because the VR cloud pass is unavailable: {ex}");
    }

    private static void ReleaseGeometry()
    {
        if (eyeMesh) Object.Destroy(eyeMesh);
        if (eyeMaterial) Object.Destroy(eyeMaterial);
        if (desktopMaterial) Object.Destroy(desktopMaterial);
        sourceMesh = null; sourceMaterial = null; eyeMesh = null; eyeMaterial = null; desktopMaterial = null;
        vertices = null; colors = null; preparedFrame = -1; previousMatrix = default;
    }

    internal static void Release()
    {
        ReleaseGeometry();
        cloud = null;
        drawFrame = -1;
        lastEyeFrame = -3;
    }
}
