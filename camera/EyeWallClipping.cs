using HarmonyLib;
using UnityEngine;

namespace CairnVR.CameraRig;

internal static class EyeWallClipping
{
    private static WallClippersRenderer renderer;
    private static Camera maskCamera;
    private static RenderTexture eyeMask, originalMask, originalTarget;
    private static Texture originalBinding;
    private static Matrix4x4 originalProjection;
    private static bool changedProjection, wasExplicit;

    internal static void Install()
    {
        var harmony = new Harmony("community.cairnvr.camera.wallclipping");
        try
        {
            harmony.Patch(AccessTools.Method(typeof(WallClippersRenderer), nameof(WallClippersRenderer.OnCameraPreCull)),
                prefix: new HarmonyMethod(typeof(EyeWallClipping), nameof(PrepareMask)));
            harmony.Patch(AccessTools.Method(typeof(CameraExtensionMethods), nameof(CameraExtensionMethods.SimpleCopyFrom)),
                postfix: new HarmonyMethod(typeof(EyeWallClipping), nameof(MatchProjection)));
        }
        catch (Exception ex)
        {
            harmony.UnpatchSelf();
            CameraPlugin.Logger.LogWarning($"Per-eye wall clipping is unavailable on this build: {ex}");
        }
    }

    private static void PrepareMask(WallClippersRenderer __instance, Camera __0)
    {
        if (!EyeLighting.IsEye(__0) || renderer || !__instance.thisCam) return;
        renderer = __instance;
        maskCamera = renderer.thisCam;
        originalMask = renderer.rt;
        // Let the game initialize its camera, format and threshold on its first pass.
        if (!renderer.initialized || !originalMask || !originalMask.IsCreated() || renderer.downscale <= 0) return;
        int width = __0.pixelWidth / renderer.downscale, height = __0.pixelHeight / renderer.downscale;
        if (width < 1 || height < 1) return;
        // Keep the desktop mask available to its camera. Giving the native pass
        // an eye-sized target also keeps it from rebuilding the desktop target.
        var desc = originalMask.descriptor;
        desc.width = width;
        desc.height = height;
        originalTarget = maskCamera.targetTexture;
        originalBinding = Shader.GetGlobalTexture(WallClippersRenderer.sid_GlobalClipScreen);
        eyeMask = RenderTexture.GetTemporary(desc);
        eyeMask.filterMode = originalMask.filterMode;
        eyeMask.wrapMode = originalMask.wrapMode;
        renderer.rt = eyeMask;
        maskCamera.targetTexture = eyeMask;
        Shader.SetGlobalTexture(WallClippersRenderer.sid_GlobalClipScreen, eyeMask);
    }

    private static void MatchProjection(Camera __0, Camera __1)
    {
        if (!maskCamera || maskCamera != __0 || !EyeLighting.IsEye(__1)) return;
        // SimpleCopyFrom copies the pose, FOV and clip planes. A tracked eye also
        // has an off-center lens. The hole depths must cover the same rays as the rock.
        if (!changedProjection)
        {
            wasExplicit = maskCamera.projectionMatrixMode == Camera.ProjectionMatrixMode.Explicit;
            originalProjection = maskCamera.projectionMatrix;
            changedProjection = true;
        }
        maskCamera.projectionMatrix = __1.projectionMatrix;
    }

    internal static void Restore()
    {
        if (changedProjection && maskCamera)
        {
            if (wasExplicit) maskCamera.projectionMatrix = originalProjection;
            else maskCamera.ResetProjectionMatrix();
        }
        if (eyeMask)
        {
            if (renderer) renderer.rt = originalMask;
            if (maskCamera) maskCamera.targetTexture = originalTarget;
            Shader.SetGlobalTexture(WallClippersRenderer.sid_GlobalClipScreen, originalBinding);
            RenderTexture.ReleaseTemporary(eyeMask);
        }
        renderer = null;
        maskCamera = null;
        eyeMask = originalMask = originalTarget = null;
        originalBinding = null;
        changedProjection = false;
    }
}
