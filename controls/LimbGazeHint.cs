using TGBTools.InputImage;
using UnityEngine;

namespace CairnVR.Controls;

internal sealed class LimbGazeHint
{
    const float DwellSeconds = .55f;
    readonly ControllerIcons.Glyph[] bumpers = { new(), new() };
    ClimbingV2PawnLimb target;
    float since;
    bool right, arm, listening;
    Vector3 gazeOrigin;
    Callout caption;

    internal void Update(ClimbingV2PawnLimb limb, bool rightSide, bool isArm, Vector3 eye)
    {
        if (target?.Pointer != limb.Pointer)
        {
            target = limb;
            since = Time.unscaledTime;
        }
        right = rightSide;
        arm = isArm;
        gazeOrigin = eye;
        if (!listening)
        {
            ControllerModels.AfterControllersRendered += Draw;
            listening = true;
        }
    }

    internal void Clear() => target = null;

    void Draw(Camera eye)
    {
        if (target == null || Time.unscaledTime - since < DwellSeconds) return;
        var anchor = ControllerModels.GetButtonAnchor(right ? "RB" : "LB");
        if (!anchor) return;

        var glyph = bumpers[right ? 1 : 0];
        if (!glyph.Sprite)
        {
            var images = InputImageManager.Instance;
            var shader = Shader.Find("Sprites/Default");
            if (images != null && images.IsReady && shader)
                glyph.Set(images.GetSprite("Gamepad", "", right ? "/rightShoulder" : "/leftShoulder",
                    default, default, false, false), shader);
        }

        string action = arm ? "Move arm" : "Move leg";
        caption ??= new Callout(null);
        caption.Set(glyph.Sprite ? action : (right ? "[RB]  " : "[LB]  ") + action, null);
        var limb = target.CurrentIKTargetPosition;
        var facing = eye.transform.rotation;
        float size = 1.7f * Mathf.Clamp(Vector3.Distance(limb, gazeOrigin), .5f, 8f);
        float side = right ? 1f : -1f;
        var point = limb + facing * (new Vector3(.032f * side, .025f, -.018f) * size);
        if (glyph.Sprite)
        {
            glyph.Draw(Matrix4x4.TRS(point, facing, Vector3.one), 0, .032f * size);
            point += facing * Vector3.right * (.022f * size * side);
        }
        caption.Draw(point, facing, !right, size);
    }

    internal void Dispose()
    {
        if (listening) ControllerModels.AfterControllersRendered -= Draw;
        foreach (var glyph in bumpers) glyph.Dispose();
        caption?.Dispose();
    }
}
