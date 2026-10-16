using Assets.Scripts.Objects;
using HarmonyLib;
using UnityEngine;

namespace StationeersMirror.Patches;

[HarmonyPatch(typeof(Thing), nameof(Thing.OnChildEnterInventory), new[] { typeof(DynamicThing) })]
internal static class SlotEnterPatch
{
    private static void Postfix(DynamicThing newChild) => MirrorOccupants.AfterEnter(newChild);
}

[HarmonyPatch(typeof(Thing), nameof(Thing.OnChildExitInventory), new[] { typeof(DynamicThing) })]
internal static class SlotExitPatch
{
    private static void Postfix(Thing __instance, DynamicThing previousChild) => MirrorOccupants.AfterExit(__instance, previousChild);
}

[HarmonyPatch(typeof(Interactable), nameof(Interactable.GetSelection), new Type[] { })]
internal static class InteractableSelectionPatch
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Interactable, object> Reported = new();

    private static void Postfix(Interactable __instance, SelectionInstance __result)
    {
        if (!(__instance.Parent is Structure owner) || !MirrorState.IsMirrored(owner) || !__instance.Collider) return;
        var collider = __instance.Collider.transform;
        // A trigger that moves with its control (MirrorControlTriggers) has a proper transform:
        // nothing is flipped, vanilla GetSelection is already right.
        if (collider.localToWorldMatrix.determinant > 0f) return;
        var axis = collider.InverseTransformVector(owner.ThingTransform.TransformVector(MirrorUtil.Axis(MirrorState.GetVariant(owner)))).normalized;
        int flipped = SelectionBoundsMath.AlignedAxis(axis.x, axis.y, axis.z);
        if (flipped < 0)
        {
            if (Plugin.DebugEnabled && !Reported.TryGetValue(__instance, out _))
            {
                Reported.Add(__instance, new object());
                Plugin.Log?.LogInfo($"Mirror selection left unchanged: off-axis collider {MirrorDrivenControls.Path(owner.ThingTransform, collider)}.");
            }
            return;
        }
        var bounds = __result.Bounds;
        var e = bounds.extents;
        e[flipped] = SelectionBoundsMath.RestoreVanillaExtent(e[flipped], collider.lossyScale[flipped]);
        bounds.extents = e;
        __result.Bounds = bounds;
    }
}
