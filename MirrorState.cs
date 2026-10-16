using System.Runtime.CompilerServices;
using Assets.Scripts.Objects;

namespace StationeersMirror;

internal static class MirrorState
{
    internal const string SaveMarker = "StationeersMirror.Mirrored";

    // Construction and save-load hooks execute on the Unity main thread. Never restore
    // a consumed variant on scope exit: it must not reach a second Thing.Create.
    internal static byte Pending;
    internal static byte ClientBuildMirrored;
    internal static byte CursorVariant;
    // The kit in the active hand when M was switched on; another item (or none) switches M off.
    internal static Assets.Scripts.Objects.Thing? CursorKit;
    internal static readonly ConditionalWeakTable<Structure, MirrorRecord> Instances = new();
    private static readonly ConditionalWeakTable<object, Variant> Carried = new();

    private sealed class Variant
    {
        internal byte Value;
    }

    internal static bool IsMirrored(Structure? structure) => GetVariant(structure) != MirrorVariant.Off;

    internal static byte GetVariant(Structure? structure) =>
        structure && Instances.TryGetValue(structure!, out var record) ? record.Variant : MirrorVariant.Off;

    internal static void Remember(object key, byte value) => Carried.GetValue(key, _ => new Variant()).Value = MirrorVariant.Normalize(value);
    internal static byte Read(object key) => Carried.TryGetValue(key, out var variant) ? variant.Value : MirrorVariant.Off;
    internal static bool TryRead(object key, out byte value)
    {
        bool found = Carried.TryGetValue(key, out var variant);
        value = found ? variant.Value : MirrorVariant.Off;
        return found;
    }
}
