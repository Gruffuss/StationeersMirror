using System.Reflection;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using HarmonyLib;

namespace StationeersMirror.Patches;

[HarmonyPatch]
internal static class SaveMarkerPatch
{
    private static MethodBase TargetMethod() => AccessTools.DeclaredMethod(typeof(Structure),
        "InitialiseSaveData", new[] { typeof(ThingSaveData).MakeByRefType() })
        ?? throw new MissingMethodException(typeof(Structure).FullName, "InitialiseSaveData(ref ThingSaveData)");

    private static void Postfix(Structure __instance, ref ThingSaveData savedData)
    {
        // Make repeated initialization idempotent, including an object unmirrored later.
        savedData.States.RemoveAll(state => state.StateName == MirrorState.SaveMarker);
        if (MirrorState.IsMirrored(__instance))
            savedData.States.Add(new InteractableState { StateName = MirrorState.SaveMarker, State = MirrorState.GetVariant(__instance) });
    }
}

[HarmonyPatch]
internal static class LoadMarkerPatch
{
    // Load<T> has the same parameter types. Select the non-generic overload explicitly.
    private static MethodBase TargetMethod() => AccessTools.GetDeclaredMethods(typeof(XmlSaveLoad))
        .Single(method => method.Name == nameof(XmlSaveLoad.Load) && !method.IsGenericMethod &&
            method.GetParameters().Select(parameter => parameter.ParameterType)
                .SequenceEqual(new[] { typeof(ThingSaveData), typeof(bool) }));

    private static void Prefix(ThingSaveData thingData) => MirrorState.Pending =
        MirrorVariant.Normalize(thingData.States.FirstOrDefault(state => state.StateName == MirrorState.SaveMarker)?.State ?? 0);

    private static void Finalizer() => MirrorState.Pending = MirrorVariant.Off;
}
