using Assets.Scripts.Objects;
using UnityEngine;

namespace StationeersMirror;

internal static class MirrorOccupants
{
    internal static bool IsInsideOccupant(Transform node, Transform root)
    {
        for (var current = node; current != null && current != root; current = current.parent)
            if (current.GetComponent<DynamicThing>() is { ParentSlot: not null })
                return true;
        return false;
    }

    internal static void AfterEnter(DynamicThing occupant)
    {
        if (occupant.RootParent is Structure structure && MirrorState.Instances.TryGetValue(structure, out var record))
        {
            // Vanilla just assigned an absolute scale; replace any previous snapshot.
            record.OccupantScales.Remove(occupant);
            Compensate(structure, record, occupant);
        }
    }

    internal static void AfterExit(Thing parent, DynamicThing occupant)
    {
        // Vanilla already restored scale to one. Drop the snapshot so a later undo
        // cannot change an item which has since left this structure.
        if (parent.RootParent is Structure structure && MirrorState.Instances.TryGetValue(structure, out var record))
            record.OccupantScales.Remove(occupant);
    }

    internal static void ApplyExisting(Structure structure, MirrorRecord record)
    {
        foreach (var occupant in structure.GetComponentsInChildren<DynamicThing>(true))
            Compensate(structure, record, occupant);
    }

    private static void Compensate(Structure structure, MirrorRecord record, DynamicThing occupant)
    {
        if (occupant.ParentSlot == null || occupant.RootParent != structure ||
            IsInsideOccupant(occupant.ThingTransform.parent, structure.ThingTransform) ||
            record.OccupantScales.ContainsKey(occupant))
            return;
        // Nested inventory inherits this counter-flip; flipping every nested item
        // would reintroduce a negative determinant on alternate inventory levels.
        int axis = MirrorUtil.CounterFlipAxis(structure, occupant.ThingTransform, record.Variant);
        if (axis < 0) return;
        record.OccupantScales.Add(occupant, occupant.ThingTransform.localScale);
        occupant.ThingTransform.localScale = MirrorUtil.Reflect(occupant.ThingTransform.localScale, (byte)(axis + 1));
    }

    internal static void Restore(Structure structure, MirrorRecord record)
    {
        var recovery = new MirrorRecovery();
        foreach (var pair in record.OccupantScales)
            if (pair.Key != null && pair.Key.ParentSlot != null && pair.Key.RootParent == structure)
                recovery.Try(() => pair.Key.ThingTransform.localScale = pair.Value);
        recovery.ThrowIfFailed();
    }
}
