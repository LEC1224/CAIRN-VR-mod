using System;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace CairnVR.Controls;

[BepInPlugin("cairn.vr.controls", "CAIRN VR climbing controls", "1.0.0")]
[BepInDependency("community.cairnvr.camera")]
public sealed class ControlsPlugin : BasePlugin
{
    internal enum RecenterControl { BothGrips, BothStickClicks, Off }
    internal static RecenterControl Recenter;
    internal static bool Gaze, Reach, Markers, Point;
    internal static float MotionRange;
    internal static bool PreviouslyRanInBackground;
    internal static BepInEx.Logging.ManualLogSource Logger = null!;
    public override void Load()
    {
        if (Environment.GetEnvironmentVariable("CAIRN_VR") != "1") return;
        Logger = Log;
        Gaze = Enabled("GAZE");
        Reach = Enabled("REACH");
        Markers = Enabled("MARKERS");
        Point = Enabled("POINT");
        Recenter = Config.Bind("Comfort", "RecenterControl", RecenterControl.BothGrips,
            "Hold the selected controller chord for 0.8 seconds to recenter. F8 remains available.").Value;
        MotionRange = Mathf.Clamp(Config.Bind("Motion", "FullReachMetres", 0.14f,
            "Controller displacement that produces full reach stick input.").Value, .04f, .45f);
        var path = Path.Combine(Path.GetDirectoryName(GetType().Assembly.Location)!, "openxr", "CairnTracking.dll");
        Tracking.Initialize(path);
        ControllerModels.Start();
        AddComponent<ClimbingControls>();
        PreviouslyRanInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Log.LogInfo($"Gamepad bridge ready. Gaze={Gaze}, reach={Reach}, route markers={Markers}, pointed reach={Point}.");
    }
    static bool Enabled(string key) => Environment.GetEnvironmentVariable("CAIRNVR_" + key) == "1";
}

[StructLayout(LayoutKind.Sequential)]
internal struct TrackedPose
{
    public Vector3 Position;
    public Quaternion Rotation;
}
[StructLayout(LayoutKind.Sequential)]
internal struct Sample
{
    public ulong Sequence;
    public uint Valid, Pressed;
    public TrackedPose Gaze, Grip, Aim;
    public float LX, LY, RX, RY, LT, RT;
    public uint Buttons;
    public TrackedPose OtherGrip;
    public uint FrameHands;
}
internal static class Tracking
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int ReadDelegate(out Sample sample, int size);
    static ReadDelegate read;
    static readonly int Size = Marshal.SizeOf<Sample>();
    public static void Initialize(string path)
    {
        var dll = NativeLibrary.Load(path);
        read = Marshal.GetDelegateForFunctionPointer<ReadDelegate>(NativeLibrary.GetExport(dll, "CairnReadTracking"));
    }
    public static bool TryRead(out Sample sample) => read!(out sample, Size) != 0;
}
