using Unity.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace CairnVR.Controls;

// XR action bindings may put Frame in tracked-controller mode. Mirror those controls into the
// game's normal Gamepad layout. A hardware gamepad takes precedence while it is being used.
internal sealed class GamepadBridge
{
    const string LayoutName = "CairnVRGamepad";
    const string LayoutJson = "{\"extend\":\"Gamepad\",\"runInBackground\":\"enabled\"}";
    bool registeredLayout;
    bool useDefaultLayout;
    InputSettings adjustedSettings;
    InputSettings.BackgroundBehavior previousBackgroundBehavior;
    Gamepad output;
    GamepadState previous;
    bool submitted;
    bool loggedBridge;
    float physicalLastUsed = -100;

    public void Apply(Sample sample, Vector2? motion)
    {
        var settings = InputSystem.settings;
        if (settings.backgroundBehavior == InputSettings.BackgroundBehavior.ResetAndDisableAllDevices)
        {
            adjustedSettings = settings;
            previousBackgroundBehavior = settings.backgroundBehavior;
            settings.backgroundBehavior = InputSettings.BackgroundBehavior.ResetAndDisableNonBackgroundDevices;
        }
        GamepadState state = new()
        {
            buttons = sample.Buttons,
            leftStick = new(sample.LX, sample.LY),
            rightStick = new(sample.RX, sample.RY),
            leftTrigger = sample.LT,
            rightTrigger = sample.RT
        };
        var pads = Gamepad.all;
        Gamepad physical = null;
        for (int p = 0; p < pads.Count; p++)
        {
            var candidate = pads[p];
            if (output != null && candidate.deviceId == output.deviceId) continue;
            var raw = ReadPhysical(candidate);
            if (raw.buttons != 0 || raw.leftStick.sqrMagnitude > .04f || raw.rightStick.sqrMagnitude > .04f
                || raw.leftTrigger > .08f || raw.rightTrigger > .08f)
            {
                physicalLastUsed = Time.unscaledTime;
                physical = candidate;
                if (motion.HasValue) state = raw;
                break;
            }
        }
        if (motion.HasValue) state.leftStick = motion.Value;
        else if ((sample.Valid & 8) == 0 || physical != null || Time.unscaledTime - physicalLastUsed < .2f)
        {
            Neutralize();
            return;
        }

        if (output == null)
        {
            if (state.buttons == 0 && state.leftStick.sqrMagnitude < .0001f && state.rightStick.sqrMagnitude < .0001f
                && state.leftTrigger < .01f && state.rightTrigger < .01f) return;
            output = CreateGamepad();
        }
        Submit(state);
    }

    // Removing a device during a scene load pushes CAIRN into its disconnected
    // gamepad context. Send all releases once and retain the device for recovery.
    public void Neutralize()
    {
        if (output != null && submitted) Submit(default);
    }

    unsafe void Submit(GamepadState state)
    {
        if (submitted && Same(state, previous)) return;
        // This non-generic event path also works when IL2CPP strips QueueStateEvent<GamepadState>.
        var buffer = StateEvent.From(output, out var eventPtr, Allocator.Temp);
        try
        {
            var stateEvent = StateEvent.From(eventPtr);
            *(GamepadState*)stateEvent->state = state;
            InputSystem.QueueEvent(eventPtr);
            if (!loggedBridge)
            {
                ControlsPlugin.Logger.LogInfo("Delivered the first XR controller event to CAIRN's gamepad input.");
                ControlsPlugin.Logger.LogInfo($"XR gamepad background capability={output.canRunInBackground}, game's input background behavior={InputSystem.settings.backgroundBehavior}, desktop focus={Application.isFocused}.");
                loggedBridge = true;
            }
            previous = state;
            submitted = true;
        }
        finally { buffer.Dispose(); }
    }

    Gamepad CreateGamepad()
    {
        if (!useDefaultLayout)
        {
            try
            {
                if (!registeredLayout)
                {
                    // IL2CPP requires a boxed nullable with HasValue=false for the optional matcher.
                    var noMatcher = new Il2CppSystem.Nullable<UnityEngine.InputSystem.Layouts.InputDeviceMatcher>();
                    InputSystem.RegisterLayout(LayoutJson, LayoutName, noMatcher);
                    registeredLayout = true;
                }
                return InputSystem.AddDevice(LayoutName, LayoutName, null).Cast<Gamepad>();
            }
            catch (System.Exception error)
            {
                ControlsPlugin.Logger.LogWarning($"Using the standard gamepad layout. Background input unavailable: {error.Message}");
                useDefaultLayout = true;
            }
        }
        return InputSystem.AddDevice("Gamepad", LayoutName, null).Cast<Gamepad>();
    }
    static bool Same(GamepadState a, GamepadState b) => a.buttons == b.buttons && a.leftStick == b.leftStick &&
        a.rightStick == b.rightStick && Mathf.Approximately(a.leftTrigger, b.leftTrigger) && Mathf.Approximately(a.rightTrigger, b.rightTrigger);

    static GamepadState ReadPhysical(Gamepad pad)
    {
        GamepadState state = new()
        {
            leftStick = pad.leftStick.ReadValue(),
            rightStick = pad.rightStick.ReadValue(),
            leftTrigger = pad.leftTrigger.ReadValue(),
            rightTrigger = pad.rightTrigger.ReadValue()
        };
        for (int i = 0; i < 14; i++) if (pad[(GamepadButton)i].isPressed) state.buttons |= 1u << i;
        return state;
    }
    public void Dispose()
    {
        if (output != null) InputSystem.RemoveDevice(output);
        output = null;
        submitted = false;
        if (registeredLayout) InputSystem.RemoveLayout(LayoutName);
        registeredLayout = false;
        if (adjustedSettings && adjustedSettings.backgroundBehavior == InputSettings.BackgroundBehavior.ResetAndDisableNonBackgroundDevices)
            adjustedSettings.backgroundBehavior = previousBackgroundBehavior;
        adjustedSettings = null;
    }
}
