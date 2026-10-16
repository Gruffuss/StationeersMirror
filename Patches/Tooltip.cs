using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.UI;
using HarmonyLib;

namespace StationeersMirror.Patches;

// While M is on, the game's own placement panel gets one line (owner chose this option,
// 2026-10-16): no new UI, gone as soon as M switches off. PlacementMode rebuilds the panel
// every frame and hands it to HandleToolTipDisplay by value (InventoryManager.cs:3066), so the
// line is added to that argument just before it is drawn. Key colour as the game's own key
// hints (Localization.cs:730).
[HarmonyPatch(typeof(Tooltip), nameof(Tooltip.HandleToolTipDisplay), new[] { typeof(PassiveTooltip) })]
internal static class MirrorTooltipPatch
{
    private static void Prefix(ref PassiveTooltip cursorPassiveTooltip)
    {
        if (MirrorState.CursorVariant == MirrorVariant.Off)
            return;
        var cursor = InventoryManager.ConstructionCursor;
        if (cursor == null || !MirrorState.IsMirrored(cursor) || cursorPassiveTooltip.Action != ActionStrings.Build)
            return;
        string line = $"Mirrored. Press <color=#FBB03B>{Plugin.ToggleKeyText}</color> to undo.";
        cursorPassiveTooltip.PlacementString = string.IsNullOrEmpty(cursorPassiveTooltip.PlacementString)
            ? line
            : cursorPassiveTooltip.PlacementString + "\n" + line;
    }
}
