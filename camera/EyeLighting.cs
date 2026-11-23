using HarmonyLib;
using UnityEngine;

namespace CairnVR.CameraRig;

// CAIRN rebuilds its screen-space shadow and local-light passes for the game
// camera and its independent scene views. A plain Camera.Render() is classified
// as Unknown and can still sample the desktop camera's last shadow texture.
internal static class EyeLighting
{
    private static Camera renderingEye;
    private static Camera gameplayCamera;
    private static bool reported;
    private static bool caveHookInstalled;
    private static CaveVolumesManager eyeCaves;
    private static int caveTextureId, ignoreCavesId;

    internal static void Install()
    {
        EyeWallClipping.Install();
        var harmony = new Harmony("community.cairnvr.camera.lighting");
        void Prefix(Type type, string method, string prefix) =>
            harmony.Patch(AccessTools.Method(type, method),
                prefix: new HarmonyMethod(typeof(EyeLighting), prefix));
        try
        {
            Prefix(typeof(MainCamera), nameof(MainCamera.GetCameraType), nameof(ClassifyEye));
            Prefix(typeof(TGBLightsManager), nameof(TGBLightsManager.IsValidCamera), nameof(AcceptLocalLights));
            Prefix(typeof(MainDirectionalLightCommandBuffers), nameof(MainDirectionalLightCommandBuffers.ShouldRebuildScreenRT), nameof(KeepShadowResolution));
        }
        catch (Exception ex)
        {
            harmony.UnpatchSelf();
            CameraPlugin.Logger.LogWarning($"Per-eye CAIRN lighting is unavailable on this build: {ex}");
        }
        try
        {
            harmony.Patch(AccessTools.Method(typeof(MainDirectionalLightCommandBuffers),
                    nameof(MainDirectionalLightCommandBuffers.BuildCommandBuffer_FarShadows)),
                postfix: new HarmonyMethod(typeof(EyeLighting), nameof(CorrectFarShadowProjection)));
        }
        catch (Exception ex)
        {
            CameraPlugin.Logger.LogWarning($"Per-eye far-shadow projection is unavailable on this build: {ex}");
        }
        var caves = new Harmony("community.cairnvr.camera.caves");
        try
        {
            caveTextureId = Shader.PropertyToID("_Caves2D");
            ignoreCavesId = Shader.PropertyToID("_IgnoreCaves2D");
            void CavePrefix(string method, string patch) => caves.Patch(AccessTools.Method(typeof(CaveVolumesManager), method),
                prefix: new HarmonyMethod(typeof(EyeLighting), patch));
            CavePrefix(nameof(CaveVolumesManager.IsValidCamera), nameof(AcceptCaveView));
            CavePrefix(nameof(CaveVolumesManager.UpdateCaveBuffers), nameof(AllowGameplayCaveQueries));
            caves.Patch(AccessTools.Method(typeof(CaveVolumesManager), nameof(CaveVolumesManager.BuildCommandBuffer)),
                postfix: new HarmonyMethod(typeof(EyeLighting), nameof(CorrectCaveProjection)));
            caveHookInstalled = true;
        }
        catch (Exception ex)
        {
            caves.UnpatchSelf();
            CameraPlugin.Logger.LogWarning($"Per-eye cave shading is unavailable on this build: {ex}");
        }
    }

    internal static void Render(Camera eye, Camera source)
    {
        renderingEye = eye;
        gameplayCamera = source;
        try
        {
            if (caveHookInstalled) RenderWithCaveMask(eye, source);
            else eye.Render();
        }
        finally
        {
            renderingEye = null;
            gameplayCamera = null;
            EyeWallClipping.Restore();
        }
    }

    private static void RenderWithCaveMask(Camera eye, Camera source)
    {
        var manager = CaveVolumesManager.Instance;
        var desktopMask = manager ? manager.caves2dTex : null;
        var previousIgnore = Shader.GetGlobalInt(ignoreCavesId);
        RenderTexture eyeMask = null;
        Texture previousTexture = null;
        try
        {
            // The game's projected cave texture remains globally bound after the
            // desktop camera finishes. Ignore it until this eye draws its own.
            Shader.SetGlobalInt(ignoreCavesId, 1);
            if (manager && manager.isActiveAndEnabled && desktopMask && source && !source.orthographic &&
                CaveVolumesManager.caveRenderingEnabled && CaveVolumesManager.caveVolumesCount > 0 &&
                manager.IsValidCamera(source))
            {
                previousTexture = Shader.GetGlobalTexture(caveTextureId);
                eyeMask = RenderTexture.GetTemporary(desktopMask.descriptor);
                eyeMask.filterMode = desktopMask.filterMode;
                eyeMask.wrapMode = desktopMask.wrapMode;
                manager.caves2dTex = eyeMask;
                eyeCaves = manager;
            }
            eye.Render();
        }
        finally
        {
            eyeCaves = null;
            if (eyeMask)
            {
                if (manager) manager.caves2dTex = desktopMask;
                Shader.SetGlobalTexture(caveTextureId, previousTexture);
                RenderTexture.ReleaseTemporary(eyeMask);
            }
            Shader.SetGlobalInt(ignoreCavesId, previousIgnore);
        }
    }

    private static bool AcceptCaveView(CaveVolumesManager __instance, Camera __0, ref bool __result)
    {
        if (!IsEye(__0)) return true;
        __result = eyeCaves != null && __instance.Pointer == eyeCaves.Pointer;
        return false;
    }

    // CAIRN also reads cave attenuation back to gameplay from its visible
    // volume list. Let the gameplay camera do that work once with its own view.
    private static bool AllowGameplayCaveQueries() => eyeCaves == null || !IsEye(Camera.current);

    private static void CorrectCaveProjection(CaveVolumesManager __instance, Camera __0)
    {
        if (eyeCaves == null || !IsEye(__0)) return;
        __instance.screenSpaceMaterial.SetMatrix(CaveVolumesManager.sid_InverseView, DepthSampleToWorld(__0));
    }

    private static void CorrectFarShadowProjection(MainDirectionalLightCommandBuffers __instance, Camera __0)
    {
        if (!IsEye(__0) || !__instance.shadowMapMaterial) return;
        __instance.shadowMapMaterial.SetMatrix(__instance.sid_InverseView, DepthSampleToWorld(__0));
    }

    private static Matrix4x4 DepthSampleToWorld(Camera eye)
    {
        // Both CAIRN shaders subtract the optical-center offsets from screen XY.
        // With negative view Z the inverse projection needs to add them instead.
        var p = eye.projectionMatrix;
        var toWorld = eye.cameraToWorldMatrix;
        var z = toWorld.GetColumn(2) - (2f * p.m02 / p.m00) * toWorld.GetColumn(0)
            - (2f * p.m12 / p.m11) * toWorld.GetColumn(1);
        toWorld.SetColumn(2, z);
        return toWorld;
    }

    internal static bool IsEye(Camera camera) =>
        renderingEye != null && camera != null && camera.Pointer == renderingEye.Pointer;

    private static bool ClassifyEye(Camera __0, ref MainCamera.RenderingCameraType __result)
    {
        if (!IsEye(__0)) return true;
        __result = MainCamera.RenderingCameraType.SceneViewCamera;
        if (!reported)
        {
            reported = true;
            CameraPlugin.Logger.LogInfo("CAIRN recognized the tracked eye for per-view lighting.");
        }
        return false;
    }

    private static bool AcceptLocalLights(TGBLightsManager __instance, Camera __0, ref bool __result)
    {
        if (!IsEye(__0) || !gameplayCamera) return true;
        // Keep the game's manager/scene eligibility. Its renderer receives the
        // tracked eye itself for frustum culling, world positions and projection.
        __result = __instance.IsValidCamera(gameplayCamera);
        return false;
    }

    private static bool KeepShadowResolution(Camera __1, ref bool __result)
    {
        if (!IsEye(__1)) return true;
        // Match CAIRN's secondary-view behavior: redraw the shared mask for
        // this projection without destroying it as eye/desktop sizes alternate.
        __result = false;
        return false;
    }
}
