using CairnVR.Controls;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TGBTools.Localization;
using UnityEngine.InputSystem;

namespace CairnVR.Tutorial;

internal readonly record struct Hint(string Control, string Text);

internal sealed class TutorialHints
{
    internal const int Capacity = 6;
    internal readonly List<Hint> Active = new(Capacity);
    readonly bool leftHanded = Option("LEFTHANDED"), move = Option("REACH"), point = Option("POINT"), markers = Option("MARKERS");
    static bool Option(string name) => Environment.GetEnvironmentVariable("CAIRNVR_" + name) == "1";

    public void Refresh()
    {
        Active.Clear();
        if (!TutorialManager.IsReady) return;
        var lessons = TutorialManager.Instance.currentTutorials;
        if (lessons == null) return;
        for (int i = lessons.Count - 1; i >= 0 && Active.Count < Capacity; --i)
        {
            var lesson = lessons[i];
            if (lesson == null || lesson.State != TutorialState.Execution) continue;
            ReadLesson(lesson);
        }
    }

    void ReadLesson(TutorialBase lesson)
    {
        var id = lesson.Id;
        bool limbLesson = id is TutorialStringIdEnum.LimbMovementAndGrabHold or TutorialStringIdEnum.ManualLimbSelectionSlidePanel;
        string grip = leftHanded ? "LeftGrip" : "RightGrip";
        if (limbLesson)
        {
            var pawn = PawnManager.IsReady ? PawnManager.Instance.ClimbingPawnController : null;
            bool reaching = pawn != null && pawn.CurrentModule != null && pawn.ReachingModule != null
                && pawn.CurrentModule.Pointer == pawn.ReachingModule.Pointer;
            if (!reaching && ControllerModels.GazeAvailable)
                Add(leftHanded ? "RightGrip" : "LeftGrip", "Look at a hand or foot\nSqueeze to select");
            if (id == TutorialStringIdEnum.LimbMovementAndGrabHold && (reaching || !ControllerModels.GazeAvailable))
            {
                if (point) Add(grip, "Hold and point at a hold");
                else if (move) Add(grip, "Hold and move to reach");
            }
        }
        if (markers && id is TutorialStringIdEnum.RoutePlanningTip or TutorialStringIdEnum.RoutePlanningSlidePanel or TutorialStringIdEnum.RoutePlanningReminderTip)
            Add(grip, "Point and mark in Eagle Eye");

        if (lesson.TryCast<PopupTutorial>() is { } popup)
        {
            int before = Active.Count;
            var rows = popup.Actions;
            if (rows != null) foreach (var row in rows)
            {
                if (row == null || row.kbm) continue;
                // The same predicates drive CAIRN's lit/available instruction rows.
                if (row.enablePredicate != null && !row.enablePredicate.Invoke()) continue;
                string caption = Caption(row.locKey);
                AddAction(row.inputAction, caption, row.holdInput);
                if (row.useDoubleInputAction && (row.enablePredicate1 == null || row.enablePredicate1.Invoke()))
                    AddAction(row.inputAction1, caption, row.holdInput1);
            }
            // Animated popup rows can contain only a sprite animation. Look up the
            // actual game action so rebinding still moves the cue to the correct button.
            if (Active.Count == before && InputManager.IsReady)
            {
                var inputs = InputManager.Instance;
                if (id is TutorialStringIdEnum.StartClimbing or TutorialStringIdEnum.StartClimbingDown)
                    AddAction(inputs.walkingActionMap?.FindAction("enterWall", false), null, false);
                else if (id == TutorialStringIdEnum.LimbMovementAndGrabHold)
                {
                    AddAction(inputs.climbingActionMap?.FindAction("lsAction", false), null, false);
                    AddAction(inputs.climbingActionMap?.FindAction("grabHoldAction", false), null, false);
                }
                else if (id is TutorialStringIdEnum.CameraMovement or TutorialStringIdEnum.CameraMovementOnWall)
                    AddAction(inputs.climbingCameraActionMap?.FindAction("rsAction", false), null, false);
            }
        }
        else if (lesson.TryCast<PanelTutorial>() is { } panel)
        {
            AddActions(panel.TutoTextInputActions, Caption(panel.TutoTextLocKey));
            AddActions(panel.ValidationTextInputActions, Caption(panel.TutoValidationTextLocKey));
        }
        else if (lesson.TryCast<TipTutorial>() is { } tip)
            AddActions(tip.InputActions, Caption(tip.LocKey));
        else if (lesson.TryCast<SlidePanelTutorial>() is { } slide)
        {
            AddActions(slide.ContentInputActions, Caption(slide.ContentLocKey));
            if (InputManager.IsReady)
            {
                var map = InputManager.Instance.tutorialPauseMap;
                if (map != null && map.enabled)
                    AddAction(map.FindAction("Validate", false), Caption(slide.ValidationLocKey), false);
            }
        }
    }

    // Long localized instructions and inline button graphics already appear in the game's HUD.
    // Reuse short native action labels on the controller when they can stand on their own.
    static string Caption(LocKeyStringId key)
    {
        if (key.Value == 0) return null;
        var localization = LocalizationManager.Instance;
        if (localization == null || !localization.IsReady) return null;
        var text = localization.Get(key)?.Trim();
        return text is { Length: > 0 and <= 28 } && text.IndexOfAny(new[] { '<', '{', '[', '\n', '\r' }) < 0 ? text : null;
    }

    void AddActions(Il2CppReferenceArray<InputAction> actions, string caption)
    {
        if (actions == null) return;
        foreach (var action in actions) AddAction(action, actions.Length == 1 ? caption : null, false);
    }

    void AddAction(InputAction action, string caption, bool hold)
    {
        if (action == null) return;
        var bindings = action.bindings;
        for (int i = 0; i < bindings.Count; ++i)
        {
            var binding = bindings[i];
            var control = ButtonFor(binding.effectivePath);
            if (control == null) continue;
            string verb = caption ?? ActionLabel(action.name, action.actionMap?.name);
            string button = ButtonName(control);
            Add(control, (hold ? "Hold " : "") + button + (verb == null ? "" : "   " + verb));
            // For a chord, label each part. Alternate independent bindings are choices.
            if (!binding.isPartOfComposite || i + 1 >= bindings.Count || !bindings[i + 1].isPartOfComposite) return;
        }
    }

    void Add(string control, string label)
    {
        if (Active.Count >= Capacity) return;
        for (int i = 0; i < Active.Count; ++i)
            if (Active[i].Control == control) return;
        Active.Add(new Hint(control, label));
    }

    internal static string ButtonFor(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int separator = path.IndexOf(">/", StringComparison.Ordinal);
        if (separator < 0 || !path.StartsWith("<", StringComparison.Ordinal)) return null;
        string layout = path.Substring(1, separator - 1);
        if (!(layout.Equals("Gamepad", StringComparison.OrdinalIgnoreCase) || layout.Contains("Gamepad", StringComparison.OrdinalIgnoreCase)
            || layout.StartsWith("XInput", StringComparison.OrdinalIgnoreCase) || layout.StartsWith("DualShock", StringComparison.OrdinalIgnoreCase)
            || layout.StartsWith("DualSense", StringComparison.OrdinalIgnoreCase))) return null;
        string component = path.Substring(separator + 2).ToLowerInvariant();
        if (component.StartsWith("leftstick/", StringComparison.Ordinal)) return "LeftStick";
        if (component.StartsWith("rightstick/", StringComparison.Ordinal)) return "RightStick";
        return component switch
        {
            "buttonsouth" or "a" => "A", "buttonwest" or "x" => "X",
            "buttoneast" or "b" => "B", "buttonnorth" or "y" => "Y",
            "dpad/up" => "Up", "dpad/down" => "Down", "dpad/left" => "Left", "dpad/right" => "Right",
            "leftshoulder" => "LB", "rightshoulder" => "RB", "lefttrigger" => "LT", "righttrigger" => "RT",
            "leftstick" => "LeftStick", "rightstick" => "RightStick", "leftstickpress" => "LeftStickPress", "rightstickpress" => "RightStickPress",
            "start" or "startbutton" => "Menu", "select" or "selectbutton" => "View", _ => null
        };
    }

    static string ButtonName(string control) => control switch
    {
        "LeftStick" => "L stick", "RightStick" => "R stick", "LeftStickPress" => "Click L stick", "RightStickPress" => "Click R stick",
        "Up" => "D-pad up", "Left" => "D-pad left", "Down" => "D-pad down", "Right" => "D-pad right", _ => control
    };

    static string ActionLabel(string action, string map) => action switch
    {
        "enterWall" => "Start climbing", "grabHoldAction" => "Grab hold", "lsAction" => map == "Climbing" ? "Move limb" : "Move",
        "rsAction" => "Look around", "cancelAction" => "Cancel move", "restAction" => "Shake arm", "runAction" => "Run",
        "runJumpAction" => "Jump", "fastWalkAction" => "Walk faster", "observationAction" => "Eagle Eye",
        "inventoryUsePiton" or "UsePiton" => "Use piton", "validatePitonExecutionAction" => "Place piton",
        "RecallClimbot" or "recallClimbot" => "Recall Climbot", "Validate" => map == "TutorialPause" ? "Continue" : "Confirm",
        "Trigger" when map == "LimbSelection" => "Select limb", "GamepadSelectLimb" => "Choose limb",
        "JumpDown" => "Jump down", "grabRope" => "Grab rope", "AscendRope" => "Climb rope",
        "Rappel" => "Rappel", "RappelJump" => "Push off", "OffBelayAction" or "OffBelay" => "Go off belay",
        "baseInteractionAction" => "Interact", _ => null
    };
}
