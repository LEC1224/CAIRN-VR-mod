using System;
using TheGameBakers.Cairn.UI;
using UnityEngine;
using PuffSelection = ClimbingGlobalTweakables.ClimberData.ClimbingSettingsData.LimbsData.Selection.ApplyPuffData.SelectionMode;

namespace CairnVR.Controls;

internal sealed class HipGestures
{
    // Head and controller poses are in the same OpenXR LOCAL space. Use the
    // real head height so both seated and standing players can reach their hip.
    readonly Func<Pose> readHead = (Func<Pose>)Type.GetType("CairnVR.CameraRig.StereoView, CairnVR.Camera", true)
        .GetProperty("TrackedHeadPose").GetGetMethod().CreateDelegate(typeof(Func<Pose>));
    readonly Hand[] hands = { new(), new() };
    CrossMenuUI menu;

    // Return sides reserved for a hip gesture, including a held chalk press
    // carried back toward the wall. The caller gives these priority over grabs.
    internal uint Update(ClimbingV2PawnController pawn, ref Sample sample)
    {
        var head = readHead();
        uint reserved = 0;
        bool shake = false;
        int chalkSide = -1;
        for (int i = 0; i < hands.Length; i++)
        {
            bool right = i == 1;
            var hand = hands[i];
            if (Quaternion.Dot(head.rotation, head.rotation) < .5f || !sample.TryGrip(right, out var grip))
            {
                hand.Reset();
                continue;
            }
            var offset = grip.Position - head.position;
            bool low = offset.y >= -.90f && offset.y <= -.37f && offset.x * offset.x + offset.z * offset.z <= .40f * .40f;
            hand.Update(low, grip.Position, Time.unscaledTime, sample.GripDown(right), sample.TriggerValue(right),
                out bool chalk, out bool recovering);
            if (hand.Reserved)
            {
                reserved |= 1u << i;
                if (right) sample.RT = 0; else sample.LT = 0;
            }
            if (chalk && chalkSide < 0) chalkSide = i;
            shake |= recovering;
        }
        if (chalkSide >= 0) Chalk(pawn, chalkSide == 1);
        else if (shake && pawn.CanTriggerRest() && !pawn.PawnControllerSwitcher.goToRappelRequest)
            pawn.TransitionToModule(pawn.RestModule, true);
        return reserved;
    }

    void Chalk(ClimbingV2PawnController pawn, bool right)
    {
        if (!menu) menu = UnityEngine.Object.FindObjectOfType<CrossMenuUI>();
        var action = CrossMenuActionType.ApplyPuff;
        if (!menu || !menu.isActiveAndEnabled || !menu.GetCrossMenuActionAvailability(action)) return;
        var selection = pawn.ClimbingTweakables.climber.climbingSettings.limbs.selection.applyPuff;
        var previous = selection.selectionMode;
        try
        {
            // ExecuteCrossMenuAction enters the normal puff module, which
            // selects its animation on entry and consumes the equipped chalk.
            selection.selectionMode = right ? PuffSelection.RightHand : PuffSelection.LeftHand;
            menu.ExecuteCrossMenuAction(action);
        }
        finally { selection.selectionMode = previous; }
    }

    internal void Reset()
    {
        foreach (var hand in hands) hand.Reset();
    }

    internal sealed class Hand
    {
        internal bool Reserved { get; private set; }
        bool tracked, wasLow, triggerHeld, pressed, consumed;
        Vector3 extreme, strokeDirection;
        int strokes;
        float lastStroke, nextShake;

        internal void Reset()
        {
            tracked = wasLow = triggerHeld = pressed = consumed = Reserved = false;
            strokes = 0;
            nextShake = 0;
        }

        internal void Update(bool low, Vector3 position, float now, bool grip, float trigger,
            out bool chalk, out bool shake)
        {
            triggerHeld = trigger >= (triggerHeld ? .30f : .55f);
            bool held = grip || triggerHeld;
            chalk = tracked && low && held && !pressed;
            // Reacquiring a held controller at hip height reserves its buttons
            // but never activates chalk until the player releases and presses.
            consumed = held && (consumed || low);
            Reserved = low || consumed;
            shake = false;
            if (!low || held || !wasLow || !tracked || now - lastStroke > .30f)
            {
                extreme = position;
                strokes = 0;
                lastStroke = now;
            }
            else if (now >= nextShake)
            {
                var displacement = position - extreme;
                // Follow the extremum until a stroke reverses, so slow drift,
                // tracking jitter, and simply lowering a hand never count.
                if (strokes > 0 && Vector3.Dot(displacement, strokeDirection) > 0)
                    extreme = position;
                else if (displacement.sqrMagnitude >= .045f * .045f)
                {
                    var direction = displacement.normalized;
                    strokes = strokes == 0 || Vector3.Dot(direction, strokeDirection) < -.55f ? strokes + 1 : 1;
                    strokeDirection = direction;
                    extreme = position;
                    lastStroke = now;
                    if (strokes >= 4)
                    {
                        shake = true;
                        strokes = 0;
                        nextShake = now + .90f;
                    }
                }
            }
            wasLow = low;
            pressed = held;
            tracked = true;
        }
    }
}
