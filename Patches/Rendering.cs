using Assets.Scripts.Objects;
using HarmonyLib;

namespace StationeersMirror.Patches;

[HarmonyPatch(typeof(RenderBatch), nameof(RenderBatch.Register), new[] { typeof(IBatchable) })]
internal static class BatchRegistrationPatch
{
    private static bool Prefix(IBatchable batchable) =>
        batchable is not ThingRenderer renderer || renderer.Parent is not Structure structure || !MirrorState.IsMirrored(structure);
}

// F9 (review): the AddRocketRenderer and RocketRendererInstance.RegisterToManager patches were
// removed as dead code: AddRocketRenderer is only called from Wall.cs:92 (walls cannot be
// mirrored) and RocketRendererInstance is only created inside it (Structure.cs:829).
