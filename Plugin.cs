using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using StationeersMods.Interface;
using UnityEngine;

namespace StationeersMirror;

[StationeersMod(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION_CONST)]
[DefaultExecutionOrder(32000)]
public sealed class Plugin : ModBehaviour
{
    private static bool _loaded;
    private static ConfigEntry<KeyboardShortcut>? _toggle;
    internal static string ToggleKeyText => _toggle?.Value.ToString() ?? "M";
    private static ConfigEntry<bool>? _debug;
    internal static ManualLogSource? Log { get; private set; }
    internal static bool DebugEnabled => _debug?.Value == true;
    private static ConfigEntry<bool>? _vanillaControls;
    private static ConfigEntry<bool>? _oneBuild;
    // Owner, 2026-10-16: the one-build behaviour of M as a toggle in settings.
    internal static bool OneBuildPerPress => _oneBuild?.Value != false;
    // Owner, 2026-10-16: a setting to choose whether controls keep their vanilla look or mirror
    // with the structure. Client-side: it changes how this game shows controls and where their
    // click boxes sit, never the structure, its connections or what is saved.
    internal static bool VanillaControls => _vanillaControls?.Value != false;

    private void Update()
    {
        if (!_loaded)
            return;
        if (_toggle?.Value.IsDown() == true && !Assets.Scripts.ConsoleWindow.IsOpen)
            Patches.CursorMirror.Toggle();
        Patches.CursorMirror.CheckKit();
    }

    // After the Animator has posed this frame.
    private void LateUpdate()
    {
        if (_loaded)
        {
            MirrorAnimatorControls.Tick();
            // Log the final corrected pose so an idle node does not appear to
            // move merely because the probe sampled before our last writeback.
            MirrorProbe.Tick();
        }
    }

    public override void OnLoaded(ContentHandler contentHandler)
    {
        base.OnLoaded(contentHandler);
        if (_loaded)
            return;

        Log = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.PLUGIN_NAME);
        _toggle = Config.Bind("Controls", "ToggleMirror", new KeyboardShortcut(KeyCode.M),
            "Mirror the active construction cursor left/right.");
        _oneBuild = Config.Bind("Controls", "OneBuildPerPress", true,
            "On: M switches off after you place, when you put the kit away, or when you take another kit; " +
            "letting go before the build finishes keeps it on. Off: M stays on until you press it again.");
        _vanillaControls = Config.Bind("Controls", "KeepControlsVanilla", true,
            "On: switches, buttons, knobs, dials and wheels on a mirrored structure keep their vanilla look, " +
            "motion and click sides. Off: they are mirrored with the structure. Applies to structures " +
            "mirrored after the change, and to all of them after a reload.");
        _debug = Config.Bind("Diagnostics", "Debug", false,
            "Log mirrored connection grids and occupied cells; warn on mismatches.");
        // F11: the mod appends a mirror byte to build and join messages, so a peer without it (or
        // with another version) would misread the stream. LaunchPadBooster's join check refuses
        // the join instead, listing the mismatch (ConnectionState.DoJoinValidateModList).
        new LaunchPadBooster.Mod(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_VERSION_CONST).Networking.Required = true;
        var harmony = new Harmony(PluginInfo.PLUGIN_GUID);
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            _loaded = true;
            Log.LogInfo($"Loaded {PluginInfo.PLUGIN_NAME} {PluginInfo.PLUGIN_VERSION_CONST}. All peers need this mod.");
        }
        catch (Exception exception)
        {
            harmony.UnpatchSelf();
            Log.LogError($"Patch installation failed; removed this mod's patches. {exception}");
            throw;
        }
    }
}
