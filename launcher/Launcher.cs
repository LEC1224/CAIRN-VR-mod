using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            bool? vr = null;
            if (args.Length > 0 && (args[0] == "--normal" || args[0] == "--vr"))
            {
                vr = args[0] == "--vr";
                args = args.Skip(1).ToArray();
            }
            string exe = args.Length > 0 ? Path.GetFullPath(args[0]) :
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cairn.exe");
            if (!File.Exists(exe) || !string.Equals(Path.GetFileName(exe), "Cairn.exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Steam must pass the path to Cairn.exe. Check the CAIRN launch options.", exe);
            string directory = Path.GetDirectoryName(exe);
            var settings = Options.Load();
            if (!vr.HasValue)
            {
                using (var chooser = new ModeChooser(settings))
                {
                    if (chooser.ShowDialog() != DialogResult.OK) return 0;
                    vr = chooser.Vr;
                    settings.Save();
                }
            }
            var arguments = new List<string>(args.Skip(1));
            var start = new ProcessStartInfo(exe) { WorkingDirectory = directory, UseShellExecute = false };
            if (vr.Value)
            {
                string layer = Path.Combine(directory, @"BepInEx\plugins\CairnVR\openxr");
                start.EnvironmentVariables.Remove("DOORSTOP_DISABLE");
                start.EnvironmentVariables["CAIRN_VR"] = "1";
                start.EnvironmentVariables["CAIRNVR_GAZE"] = settings.Gaze ? "1" : "0";
                start.EnvironmentVariables["CAIRNVR_REACH"] = settings.Reach ? "1" : "0";
                start.EnvironmentVariables["CAIRNVR_POINT"] = settings.Point ? "1" : "0";
                start.EnvironmentVariables["CAIRNVR_LEFTHANDED"] = settings.LeftHanded ? "1" : "0";
                start.EnvironmentVariables["CAIRNVR_MARKERS"] = settings.Markers ? "1" : "0";
                Prepend(start, "XR_API_LAYER_PATH", layer);
                Prepend(start, "XR_ENABLE_API_LAYERS", "XR_APILAYER_CAIRN_controls");
                arguments.Add("-force-d3d11");
            }
            else
            {
                start.EnvironmentVariables["DOORSTOP_DISABLE"] = "TRUE";
                start.EnvironmentVariables["CAIRN_VR"] = "0";
            }
            arguments.Add("--doorstop-enabled");
            arguments.Add(vr.Value ? "true" : "false");
            start.Arguments = string.Join(" ", arguments.Select(QuoteArgument));
            using (Process game = Process.Start(start))
            {
                game.WaitForExit();
                return game.ExitCode;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "CAIRN launch error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Prepend(ProcessStartInfo start, string key, string value)
    {
        string old = start.EnvironmentVariables[key];
        if (!string.IsNullOrEmpty(old) && old.Split(';').Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        start.EnvironmentVariables[key] = string.IsNullOrEmpty(old) ? value : value + ";" + old;
    }

    // Win32 takes one command line. Escape embedded quotes and trailing backslashes for argv decoding.
    private static string QuoteArgument(string arg)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal sealed class Options
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"CairnVR\controls.ini");
    public bool Gaze, Reach, Point, LeftHanded, Markers;

    public static Options Load()
    {
        var options = new Options();
        if (!File.Exists(SettingsFile)) return options;
        string[] lines = File.ReadAllLines(SettingsFile);
        options.Gaze = lines.Contains("gaze=1");
        options.Point = lines.Contains("point=1");
        options.Reach = !options.Point && lines.Contains("reach=1");
        options.LeftHanded = lines.Contains("lefthanded=1");
        options.Markers = lines.Contains("markers=1");
        return options;
    }

    public void Save()
    {
        string directory = Path.GetDirectoryName(SettingsFile);
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            File.WriteAllLines(temporary, new[] { "gaze=" + (Gaze ? "1" : "0"),
                "reach=" + (Reach ? "1" : "0"), "point=" + (Point ? "1" : "0"),
                "lefthanded=" + (LeftHanded ? "1" : "0"), "markers=" + (Markers ? "1" : "0") });
            if (File.Exists(SettingsFile)) File.Replace(temporary, SettingsFile, null);
            else File.Move(temporary, SettingsFile);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class ModeChooser : Form
{
    public bool Vr { get; private set; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Pad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PadState { public uint Packet; public Pad Pad; }
    [DllImport("xinput1_4.dll")]
    private static extern int XInputGetState(int index, out PadState state);
    private readonly Timer gamepad = new Timer { Interval = 40 };
    private readonly ushort[] previous = new ushort[4];
    private readonly bool[] connected = new bool[4];
    private readonly Control[] choices;

    public ModeChooser(Options options)
    {
        Text = "Play CAIRN";
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(380, 340);
        var normal = new Button { Text = "Play normally", Location = new Point(18, 16), Size = new Size(166, 46), TabIndex = 0 };
        var vr = new Button { Text = "Play in VR", Location = new Point(193, 16), Size = new Size(168, 46), TabIndex = 1 };
        var movement = new GroupBox { Text = "Limb movement in VR", Location = new Point(18, 76), Size = new Size(343, 120), TabIndex = 2 };
        var stick = ReachMode(movement, "Thumbstick", 23, !options.Reach && !options.Point);
        var reach = ReachMode(movement, "Move the matching controller", 53, options.Reach);
        var point = ReachMode(movement, "Point with the matching controller", 83, options.Point);
        var group = new GroupBox { Text = "VR controls", Location = new Point(18, 203), Size = new Size(343, 121), TabIndex = 3 };
        var gaze = Option(group, "Select limbs with eye tracking", 23, options.Gaze);
        var markers = Option(group, "Place route markers while scouting", 53, options.Markers);
        var handed = Option(group, "Mark routes with the left controller", 83, options.LeftHanded);
        choices = new Control[] { normal, vr, stick, reach, point, gaze, markers, handed };
        normal.Click += delegate { Vr = false; DialogResult = DialogResult.OK; };
        vr.Click += delegate { Vr = true; DialogResult = DialogResult.OK; };
        FormClosing += delegate
        {
            options.Gaze = gaze.Checked;
            options.Reach = reach.Checked;
            options.Point = point.Checked;
            options.LeftHanded = handed.Checked;
            options.Markers = markers.Checked;
        };
        Controls.AddRange(new Control[] { normal, vr, movement, group });
        Shown += delegate { normal.Focus(); };
        KeyPreview = true;
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
        };
        gamepad.Tick += Poll;
        gamepad.Start();
    }

    private static RadioButton ReachMode(Control parent, string label, int top, bool value)
    {
        var radio = new RadioButton { Text = label, Left = 13, Top = top, Width = 320, Height = 26,
            Checked = value, TabIndex = parent.Controls.Count };
        parent.Controls.Add(radio);
        return radio;
    }

    private static CheckBox Option(Control group, string label, int top, bool value)
    {
        var box = new CheckBox { Text = label, Left = 13, Top = top, Width = 320, Height = 27,
            Checked = value, TabIndex = group.Controls.Count };
        group.Controls.Add(box);
        return box;
    }

    private void Poll(object sender, EventArgs e)
    {
        if (!ContainsFocus) return;
        for (int i = 0; i < previous.Length; i++)
        {
            PadState current;
            try { if (XInputGetState(i, out current) != 0) { connected[i] = false; continue; } }
            catch (DllNotFoundException) { gamepad.Stop(); return; }
            ushort buttons = current.Pad.Buttons;
            if (current.Pad.LX < -16000) buttons |= 4;
            if (current.Pad.LX > 16000) buttons |= 8;
            if (current.Pad.LY < -16000) buttons |= 2;
            if (current.Pad.LY > 16000) buttons |= 1;
            ushort pressed = connected[i] ? (ushort)(buttons & ~previous[i]) : (ushort)0;
            previous[i] = buttons;
            connected[i] = true;
            int selected = Math.Max(0, Array.FindIndex(choices, control => control.Focused));
            if ((pressed & 0x2000) != 0) { Close(); return; }
            if ((pressed & 0x1000) != 0)
            {
                var button = choices[selected] as Button;
                if (button != null) { button.PerformClick(); return; }
                var box = choices[selected] as CheckBox;
                if (box != null) box.Checked = !box.Checked;
                else ((RadioButton)choices[selected]).Checked = true;
            }
            else if ((pressed & (2 | 8)) != 0) choices[(selected + 1) % choices.Length].Focus();
            else if ((pressed & (1 | 4)) != 0) choices[(selected + choices.Length - 1) % choices.Length].Focus();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) gamepad.Dispose();
        base.Dispose(disposing);
    }
}
