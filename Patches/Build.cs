using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using HarmonyLib;

namespace StationeersMirror.Patches;

[HarmonyPatch(typeof(Structure), nameof(Structure.OnAssignedReference), new Type[] { })]
internal static class AssignedReferencePatch
{
    private static void Prefix(Structure __instance)
    {
        if (MirrorState.Pending == MirrorVariant.Off || __instance.IsCursor)
            return;
        // Consume before Apply, including refusal/error paths and reentrant callbacks.
        byte variant = MirrorState.Pending;
        MirrorState.Pending = MirrorVariant.Off;
        try
        {
            if (!MirrorState.IsMirrored(__instance))
                MirrorUtil.Apply(__instance, variant);
        }
        catch (Exception error) { MirrorUtil.ReportFailure(__instance, error); }
    }

    private static void Postfix(Structure __instance)
    {
        try
        {
            if (MirrorState.IsMirrored(__instance))
                MirrorUtil.RefreshConnections(MirrorUtil.GetConnections(__instance));
            MirrorUtil.CheckRegistered(__instance);
        }
        catch (Exception error)
        {
            // A diagnostic/cache failure must not abort Thing.Create either.
            try { MirrorUtil.SetRegisteredMirror(__instance, MirrorVariant.Off); }
            catch (Exception recoveryError) { error = new AggregateException(error, recoveryError); }
            MirrorUtil.ReportFailure(__instance, error);
        }
    }
}

[HarmonyPatch(typeof(InventoryManager), "UsePrimaryComplete", new Type[] { })]
internal static class UsePrimaryCompletePatch
{
    private static void Prefix(out byte __state)
    {
        __state = MirrorState.ClientBuildMirrored;
        byte variant = MirrorState.GetVariant(InventoryManager.ConstructionCursor);
        MirrorState.Pending = GameManager.RunSimulation ? variant : MirrorVariant.Off;
        MirrorState.ClientBuildMirrored = !GameManager.RunSimulation ? variant : MirrorVariant.Off;
    }

    private static void Finalizer(byte __state)
    {
        MirrorState.Pending = MirrorVariant.Off;
        MirrorState.ClientBuildMirrored = __state;
    }

    // M applies to one build (owner, 2026-10-16: "mirror should be on a per build basis not
    // for all"): once a placement completes, switch the toggle off and restore the preview.
    private static void Postfix()
    {
        if (!Plugin.OneBuildPerPress || MirrorState.CursorVariant == MirrorVariant.Off)
            return;
        MirrorState.CursorVariant = MirrorVariant.Off;
        MirrorState.CursorKit = null;
        CursorMirror.Synchronize();
    }
}

[HarmonyPatch(typeof(CreateStructureInstance), MethodType.Constructor, new[] { typeof(Structure), typeof(Structure) })]
internal static class RebuildInstancePatch
{
    private static void Postfix(CreateStructureInstance __instance, Structure oldPrefab) =>
        MirrorState.Remember(__instance, MirrorState.GetVariant(oldPrefab));
}

[HarmonyPatch(typeof(Constructor), nameof(Constructor.SpawnConstruct), new[] { typeof(CreateStructureInstance) })]
internal static class SpawnConstructPatch
{
    private static void Prefix(CreateStructureInstance instance)
    {
        // Fresh construction instances inherit the pending build/message scope.
        // Rebuild instances carry an explicit variant, including zero (unmirrored).
        if (MirrorState.TryRead(instance, out byte variant))
            MirrorState.Pending = variant;
    }

    private static void Finalizer() => MirrorState.Pending = MirrorVariant.Off;
}
