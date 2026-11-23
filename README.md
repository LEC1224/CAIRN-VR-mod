# CAIRN VR

Play CAIRN in third person with a head-tracked stereoscopic view. Aava uses the game's original climbing controls. You can also pick a limb with your eyes and guide its reach with one controller.

## Playing on this PC

In Steam, open **Library → CAIRN → Play**. Choose **Play normally** for the original game. For VR, connect your headset to SteamVR, check that SteamVR can see it, and choose **Play in VR**. For a Steam Frame streaming from a PC, use the CAIRN installation on the PC. SteamVR should be the PC's active OpenXR runtime.

The Windows chooser appears on the PC desktop. With SteamVR already running, Steam may show a **VR Theater Screen** notice first. Select **OK** to continue to the chooser. If you launch from inside the headset, open SteamVR's desktop view to reach the chooser, then return to the game. SteamVR can pause the view and controller input while the Frame is in standby.

The chooser starts with thumbstick climbing and remembers your choices between sessions. Menus, button prompts and subtitles follow your view on a stereo panel. You can turn your head or lean independently of the game's camera. If you can look around but the buttons don't respond, click CAIRN's window on the PC to restore input focus.

Both tracked controllers are visible during ordinary play. The mod reads the Steam Frame models and their physical button locations from the local SteamVR installation. A simplified model is available when the device model files are unavailable. Each controller disappears independently if tracking is lost.

In the tutorial, look at your controllers for the current lesson's button labels. A ring marks the button and a line connects it to the instruction. The default **Start climbing** prompt points to **X** on the right Frame controller and follows the game's binding if you remap it. With eye selection enabled and gaze tracking active, the limb-selection lesson points to the non-reaching hand's grip. Controller movement, pointed reach and route-marking instructions depend on the options selected in the launcher. The labels reuse CAIRN's lettering and short localized instructions where available. The added VR gesture instructions are in English.

| Control | How to use it |
| --- | --- |
| Standard climbing | Use CAIRN's usual sticks, buttons and grab control. You can use the Frame controllers or a separate gamepad. |
| Eye selection | Enable **Select limbs with eye tracking**. Look at one visible hand or foot. When the target indicator appears, squeeze the non-reaching hand's grip to start reaching with that limb. If two endpoints overlap, change your viewing angle. The runtime must supply eye gaze. Manual limb selection is always available. |
| Controller movement | At the wall, hold the reaching hand's grip and move that controller up, down or sideways. CAIRN can choose the limb from your first movement, or you can select one yourself. Release the grip to reposition your hand. Use CAIRN's usual grab control to place the limb. |
| Pointed reach | Choose **Point at the rock while gripping**. With a limb selected, hold the reaching hand's grip and point at the intended spot. Release the grip to fine-tune with the stick, then use the usual grab control. CAIRN determines reach and whether the hold works. |
| Route markers | Enable **Place route markers while scouting**. In Eagle Eye, aim at the rock and squeeze the reaching hand's grip to place a mark. Point at the same mark and squeeze again to remove it. Up to eight markers last for the current scene. |
| Handedness | The right hand reaches by default. **Use left hand to reach and mark routes** swaps the two gesture roles. |
| Recenter | Sit or stand as you intend to play, face forward and hold both grips for about one second, or press **F8** on the PC. |

The camera can steady the viewpoint through falls and fade when the headset would enter rock. Settings for these behaviors, eye resolution and head-oriented audio are in `BepInEx/config/community.cairnvr.camera.cfg` after the first VR launch. `MaxEyeDimension` defaults to 1600 pixels on the longest edge. Both eyes keep their optical proportions. Lower this value and relaunch if frame pacing is uneven. Raise it for more detail, or set it to 0 to use SteamVR’s recommended dimensions multiplied by `RenderScale`. The plugin log records the requested and actual eye sizes. Gesture sensitivity and the recenter chord are in `BepInEx/config/cairn.vr.controls.cfg`. If the headset stays in SteamVR Home, check the status on the game's desktop window and the plugin messages in `BepInEx/LogOutput.log`.

## Reinstalling on Windows

The chooser is already configured on the development PC. For a clean install elsewhere, extract `CairnVR.zip` beside `Cairn.exe`. If that installation already uses BepInEx or Doorstop, keep a copy of its loader configuration and merge the plugin folders. In CAIRN's **Properties → General → Launch Options**, enter the quoted full path to the launcher followed by `%command%`. For the default Steam library, the value is

```text
"C:\Program Files (x86)\Steam\steamapps\common\Cairn\CairnVRLauncher.exe" %command%
```

The first VR launch generates assemblies for the installed game and can take longer. To go straight back to regular Steam launches, remove the launcher path and `%command%` wrapper from Launch Options, preserving any arguments you had before. The packaged loader defaults to disabled when starting the game directly.

## Standalone Steam Frame

The archive includes an experimental launcher for the headset's local SteamOS/Proton installation. Extract the same archive beside the headset's copy of `Cairn.exe`. In the headset's CAIRN Launch Options, enter the following with the actual path to its Steam library.

```sh
bash "/absolute/path/to/Cairn/cairn-frame.sh" %command%
```

The script offers a chooser when Zenity and a graphical session are available. To select VR directly, put `--vr` between the script path and `%command%`. The Windows game and plugins then run through Proton and the headset's x86-64 translation. The wrapper was tested with a simulated Steam command. Local OpenXR device support, frame rate, controller tracking and eye tracking on the headset still require a physical Frame test.

## Build and verification

The Windows build targets Steam app 1588550, build 26753169, Unity 6000.0.72f1, IL2CPP x64 and Direct3D 11. Building the plugins needs a local CAIRN installation with BepInEx 6 IL2CPP-generated interop assemblies. The repository contains the original mod sources. The package contains the loader and runtime dependencies and excludes the game, player data and generated game assemblies.

On Windows with .NET SDK, Visual Studio x64 C++ build tools, Git and the MinGW-w64 x64 C compiler available, run `.\package.ps1`. For another Steam library pass `-GameDir 'D:\SteamLibrary\steamapps\common\Cairn'`. The package is written to `dist/CairnVR.zip`.

Windows verification used Steam's actual Play action for both modes. With a process-local test OpenXR runtime, the game submitted distinct left- and right-eye images, rendered its mountain and menu text, received gaze and hand poses, and accepted a simulated controller button to enter its main menu.

An RTX 3080 test ran for 12 minutes in the climbing gym with the desktop at 2560 × 1440. The test runtime requested 3036 × 3036 per eye and received 1600 × 1600. Controller motion started a reach from rest, moved the selected hand and placed it using the game's grab button. The body and outfit matched the desktop pose in all 5,760 sampled frames. The PC submitted stereo frames throughout the run. This test used simulated tracking.

The UI and lighting update was tested in the installed game using simulated head and controller poses. The menu stayed within one pixel of its position in each eye during rotation and leaning. During a 20° turn, the bright summit and foreground rock highlight followed the scenery. Controller silhouettes matched the projection of Valve's model to within one pixel in the tested poses. Tests using isolated tutorial records exercised the default X prompt, a remap to Y, and gaze and manual limb hints without advancing a player save.

In VR, the upper weather clouds use the distance from the viewer to the cloud to determine their opacity. Cave shading uses a separate mask for each eye. To check these effects, the installed game rendered the same frozen weather through head turns and tilts. The tests also tracked the sun and its halo under a clear sky and placed a temporary cave volume over a test surface.

During PC streaming, the player confirmed that the mountain and CAIRN’s main menu were visible inside the Steam Frame. Gaze selection and controller-guided limb placement during a climb, comfort during play and standalone performance still need playtesting.

In a separate diagnostic, SteamVR loaded the controller and gaze bindings, entered the OpenXR Visible state and accepted 442 stereo frames at 3036 × 3036 per eye. CAIRN kept running when SteamVR returned to standby. This diagnostic temporarily allowed rendering in standby and then restored SteamVR's original setting. The renderer uses a separate D3D11 device for SteamVR to avoid a startup stall observed on the test PC's RTX 3080.
