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

    internal static void Install()
    {
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
    }

    internal static void Render(Camera eye, Camera source)
    {
        renderingEye = eye;
        gameplayCamera = source;
        try { eye.Render(); }
        finally { renderingEye = null; gameplayCamera = null; }
    }

    private static bool IsEye(Camera camera) =>
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
