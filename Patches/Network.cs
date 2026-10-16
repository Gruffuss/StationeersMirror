using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using HarmonyLib;

namespace StationeersMirror.Patches;

[HarmonyPatch(typeof(CreateStructureMessage), MethodType.Constructor, new Type[] { })]
internal static class CreateMessagePatch
{
    private static void Postfix(CreateStructureMessage __instance) =>
        MirrorState.Remember(__instance, MirrorState.ClientBuildMirrored);
}

[HarmonyPatch(typeof(CreateStructureMessage), nameof(CreateStructureMessage.Serialize), new[] { typeof(RocketBinaryWriter) })]
internal static class SerializeCreateMessagePatch
{
    private static void Postfix(CreateStructureMessage __instance, RocketBinaryWriter writer) =>
        writer.WriteByte(MirrorState.Read(__instance));
}

[HarmonyPatch(typeof(CreateStructureMessage), nameof(CreateStructureMessage.Deserialize), new[] { typeof(RocketBinaryReader) })]
internal static class DeserializeCreateMessagePatch
{
    private static void Postfix(CreateStructureMessage __instance, RocketBinaryReader reader) =>
        MirrorState.Remember(__instance, reader.ReadByte());
}

[HarmonyPatch(typeof(CreateStructureMessage), nameof(CreateStructureMessage.Process), new[] { typeof(long) })]
internal static class ProcessCreateMessagePatch
{
    private static void Prefix(CreateStructureMessage __instance) => MirrorState.Pending = MirrorState.Read(__instance);
    private static void Finalizer() => MirrorState.Pending = MirrorVariant.Off;
}

[HarmonyPatch(typeof(Thing), nameof(Thing.SerializeOnJoin), new[] { typeof(RocketBinaryWriter) })]
internal static class SerializeJoinPatch
{
    private static void Postfix(Thing __instance, RocketBinaryWriter writer)
    {
        if (__instance is Structure structure)
            writer.WriteByte(MirrorState.GetVariant(structure));
    }
}

[HarmonyPatch(typeof(Thing), nameof(Thing.DeserializeOnJoin), new[] { typeof(RocketBinaryReader) })]
internal static class DeserializeJoinPatch
{
    private static void Postfix(Thing __instance, RocketBinaryReader reader)
    {
        if (__instance is Structure structure)
        {
            // Keep wire errors outside the mirror catch. Always consume our byte
            // before running code which could fail, then let subclass reads continue.
            byte variant = reader.ReadByte();
            try { MirrorUtil.SetRegisteredMirror(structure, variant); }
            catch (Exception error) { MirrorUtil.ReportFailure(structure, error); }
        }
    }
}

// SpawnConstruct has a separate client branch used outside UsePrimaryComplete.
// Carry rebuild state through that message too, before OnServer.Create registers it.
[HarmonyPatch(typeof(ConstructionCreationMessage), MethodType.Constructor, new[] { typeof(CreateStructureInstance) })]
internal static class ConstructionMessagePatch
{
    private static void Postfix(ConstructionCreationMessage __instance, CreateStructureInstance instance) =>
        MirrorState.Remember(__instance, MirrorState.TryRead(instance, out byte variant) ? variant : MirrorState.Pending);
}

[HarmonyPatch(typeof(ConstructionCreationMessage), nameof(ConstructionCreationMessage.Serialize), new[] { typeof(RocketBinaryWriter) })]
internal static class SerializeConstructionMessagePatch
{
    private static void Postfix(ConstructionCreationMessage __instance, RocketBinaryWriter writer) =>
        writer.WriteByte(MirrorState.Read(__instance));
}

[HarmonyPatch(typeof(ConstructionCreationMessage), nameof(ConstructionCreationMessage.Deserialize), new[] { typeof(RocketBinaryReader) })]
internal static class DeserializeConstructionMessagePatch
{
    private static void Postfix(ConstructionCreationMessage __instance, RocketBinaryReader reader) =>
        MirrorState.Remember(__instance, reader.ReadByte());
}

[HarmonyPatch(typeof(ConstructionCreationMessage), nameof(ConstructionCreationMessage.Process), new[] { typeof(long) })]
internal static class ProcessConstructionMessagePatch
{
    private static void Prefix(ConstructionCreationMessage __instance) => MirrorState.Pending = MirrorState.Read(__instance);
    private static void Finalizer() => MirrorState.Pending = MirrorVariant.Off;
}
