using Mono.Cecil;

// Read metadata only: never load or execute the game or Unity assemblies.
if (args.Length != 2)
    throw new ArgumentException("Usage: Verify <mod dll> <Assembly-CSharp.dll>");
using var resolver = new DefaultAssemblyResolver();
string managed = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
resolver.AddSearchDirectory(managed);
resolver.AddSearchDirectory(Path.GetFullPath(Path.Combine(managed, "../../BepInEx/core")));
using var mod = AssemblyDefinition.ReadAssembly(args[0], new ReaderParameters { AssemblyResolver = resolver });
using var game = AssemblyDefinition.ReadAssembly(args[1], new ReaderParameters { AssemblyResolver = resolver });
foreach (var reference in mod.MainModule.GetMemberReferences().OfType<MethodReference>()
    .Where(m => m.DeclaringType.Namespace == "System.Numerics"))
    Require(reference.Resolve() != null, "Installed game runtime lacks Numerics member " + reference.FullName);
Console.WriteLine("PASS shared control math Numerics methods resolve against the installed game runtime.");
var targets = new Dictionary<string, MethodDefinition>();
foreach (var patch in mod.MainModule.Types.Where(type => type.Namespace == "StationeersMirror.Patches"))
{
    var attribute = patch.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == "HarmonyLib.HarmonyPatch");
    if (attribute == null)
        continue;
    string targetType;
    string targetName;
    string[] parameters;
    bool nonGenericOnly = false;
    if (patch.Name == "SaveMarkerPatch")
    {
        // The TargetMethod body must explicitly request the ref overload.
        var body = patch.Methods.Single(m => m.Name == "TargetMethod").Body.Instructions;
        Require(body.Any(i => i.Operand is MethodReference m && m.Name == "MakeByRefType"), "Save target lost ref lookup");
        Require(body.Any(i => Equals(i.Operand, "InitialiseSaveData")), "Save target name changed");
        targetType = "Assets.Scripts.Objects.Structure";
        targetName = "InitialiseSaveData";
        parameters = ["Assets.Scripts.Objects.ThingSaveData&"];
    }
    else if (patch.Name == "LoadMarkerPatch")
    {
        var methods = patch.Methods.Concat(patch.NestedTypes.SelectMany(type => type.Methods));
        Require(methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is MethodReference method && method.Name == "get_IsGenericMethod"),
            "Load TargetMethod no longer excludes the generic overload");
        nonGenericOnly = true;
        targetType = "Assets.Scripts.Serialization.XmlSaveLoad";
        targetName = "Load";
        parameters = ["Assets.Scripts.Objects.ThingSaveData", "System.Boolean"];
    }
    else
    {
        var arguments = attribute.ConstructorArguments;
        targetType = ((TypeReference)arguments[0].Value).FullName;
        targetName = arguments[1].Type.FullName == "HarmonyLib.MethodType" ? ".ctor" : (string)arguments[1].Value;
        parameters = ((CustomAttributeArgument[])arguments[2].Value).Select(a => ((TypeReference)a.Value).FullName).ToArray();
    }
    var type = game.MainModule.GetType(targetType) ?? throw new Exception($"Missing type {targetType}");
    var candidates = type.Methods.Where(m => m.Name == targetName && (!nonGenericOnly || !m.HasGenericParameters) &&
        m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)).ToArray();
    Require(candidates.Length == 1, $"{patch.Name}: expected exactly one declared {targetType}.{targetName}({string.Join(", ", parameters)})");
    var target = candidates[0];
    foreach (var hook in patch.Methods.Where(m => m.Name is "Prefix" or "Postfix" or "Finalizer"))
    {
        foreach (var result in hook.Parameters.Where(p => p.Name == "__result"))
            Require(result.ParameterType.FullName == target.ReturnType.FullName,
                $"{patch.Name}.{hook.Name}: result type mismatch");
        foreach (var parameter in hook.Parameters.Where(p => !p.Name.StartsWith("__", StringComparison.Ordinal)))
        {
            var original = target.Parameters.SingleOrDefault(p => p.Name == parameter.Name);
            Require(original != null, $"{patch.Name}.{hook.Name}: target lacks parameter {parameter.Name}");
            // Harmony lets a hook take a by-value argument by ref to modify it (MirrorTooltipPatch).
            Require(original!.ParameterType.FullName == parameter.ParameterType.FullName ||
                    original.ParameterType.FullName + "&" == parameter.ParameterType.FullName,
                $"{patch.Name}.{hook.Name}: parameter type mismatch for {parameter.Name}");
        }
    }
    targets.Add(patch.Name, target);
    Console.WriteLine($"PASS {patch.Name}: {(target.IsPrivate ? "private" : target.IsFamily ? "protected" : "public")} {(target.IsStatic ? "static " : "")}{target.FullName}");
}
string[] required = ["AssignedReferencePatch", "UsePrimaryCompletePatch", "RebuildInstancePatch", "SpawnConstructPatch",
    "SaveMarkerPatch", "LoadMarkerPatch", "CreateMessagePatch", "SerializeCreateMessagePatch", "DeserializeCreateMessagePatch",
    "ProcessCreateMessagePatch", "SerializeJoinPatch", "DeserializeJoinPatch", "ConstructorCursorPatch", "StructureCursorPatch",
    "BatchRegistrationPatch", "MirrorTooltipPatch", "ConstructionMessagePatch",
    "SerializeConstructionMessagePatch", "DeserializeConstructionMessagePatch", "ProcessConstructionMessagePatch",
    "SlotEnterPatch", "SlotExitPatch", "InteractableSelectionPatch"];
Require(required.All(targets.ContainsKey), "A required patch is missing");
Require(!targets.ContainsKey("AddRocketRendererPatch") && !targets.ContainsKey("RocketRegistrationPatch"), "F9: dead rocket-renderer patches are back");
{
    var pluginType = mod.MainModule.Types.Single(t => t.Name == "Plugin");
    var loaded = pluginType.Methods.Single(m => m.Name == "OnLoaded").Body.Instructions;
    Require(loaded.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Newobj && i.Operand is MethodReference c && c.DeclaringType.FullName == "LaunchPadBooster.Mod") &&
        loaded.Any(i => i.Operand is MethodReference m && m.Name == "set_Required" && i.Previous.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_1),
        "F11: mod not registered with LaunchPadBooster as multiplayer-required");
    var update = pluginType.Methods.Single(m => m.Name == "Update").Body.Instructions;
    Require(update.Any(i => i.Operand is MethodReference m && m.Name == "get_IsOpen" && m.DeclaringType.Name == "ConsoleWindow"),
        "F7: M toggle no longer ignores the open console");
    Console.WriteLine("PASS F11 required-mod registration, F7 console gate, F9 dead patches absent.");
    var apply = mod.MainModule.Types.Single(t => t.Name == "MirrorUtil").Methods.Single(m => m.Name == "Apply").Body.Instructions;
    Require(apply.Any(i => i.Operand is MethodReference m && m.Name == "get_VanillaControls"),
        "KeepControlsVanilla setting no longer consulted when mirroring");
    Console.WriteLine("PASS KeepControlsVanilla consulted by Apply.");
    bool Consults(string type, string method) => mod.MainModule.Types.Single(t => t.Name == type)
        .Methods.Single(m => m.Name == method).Body.Instructions
        .Any(i => i.Operand is MethodReference m && m.Name == "get_OneBuildPerPress");
    Require(Consults("UsePrimaryCompletePatch", "Postfix") && Consults("CursorMirror", "CheckKit"),
        "OneBuildPerPress setting no longer consulted after placement and on kit change");
    Console.WriteLine("PASS OneBuildPerPress consulted after placement and on kit change.");
}
foreach (string name in new[] { "UsePrimaryCompletePatch", "SpawnConstructPatch", "LoadMarkerPatch", "ProcessCreateMessagePatch", "ProcessConstructionMessagePatch" })
{
    var patch = mod.MainModule.Types.Single(t => t.Name == name);
    var finalizer = patch.Methods.Single(m => m.Name == "Finalizer");
    var instructions = finalizer.Body.Instructions;
    Require(instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Stsfld &&
        i.Operand is FieldReference field && field.Name == "Pending" &&
        i.Previous.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0), $"{name} does not clear Pending in finalizer");
}
Console.WriteLine($"PASS {targets.Count} exact patch signatures, hook parameters, and five Pending finalizers.");
int fieldChecks = 0;
foreach (var instruction in mod.MainModule.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions))
{
    if (instruction.Operand is not GenericInstanceMethod call || call.Name != "FieldRefAccess" || call.GenericArguments.Count != 2)
        continue;
    string name = instruction.Previous.Operand as string ?? throw new Exception("Expected a literal field name before FieldRefAccess");
    var type = game.MainModule.GetType(call.GenericArguments[0].FullName);
    var field = type.Fields.SingleOrDefault(f => f.Name == name);
    Require(field != null && field.FieldType.FullName == call.GenericArguments[1].FullName,
        $"AccessTools field mismatch: {type.FullName}.{name}");
    fieldChecks++;
}
Require(fieldChecks == 5, "Expected five checked private/protected field accessors");
Console.WriteLine($"PASS {fieldChecks} AccessTools field names and types.");
var buildEligibility = mod.MainModule.Types.Single(t => t.Name == "MirrorUtil").Methods.Single(m => m.Name == "CanBuildMirrored");
var buildExcluded = buildEligibility.Body.Instructions.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Isinst)
    .Select(i => ((TypeReference)i.Operand).FullName).ToHashSet();
Require(buildExcluded.Contains("Assets.Scripts.Objects.Pipes.Pipe") && buildExcluded.Contains("Assets.Scripts.Objects.Electrical.Cable"),
    "building refuses pipes and cables (owner, 2026-10-16)");
var eligibility = mod.MainModule.Types.Single(t => t.Name == "MirrorUtil").Methods.Single(m => m.Name == "CanMirror");
var excludedTypes = eligibility.Body.Instructions.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Isinst)
    .Select(i => ((TypeReference)i.Operand).FullName).ToHashSet();
Require(excludedTypes.Contains("Networks.INetworkedRocketPart") && !excludedTypes.Contains("Objects.Rockets.IRocketInternals"),
    "Rocket eligibility must exclude hulls, not ordinary IRocketInternals devices");
Console.WriteLine("PASS rocket hull exclusion retained; IRocketInternals devices are not excluded.");
// The control adapters use cached reflection rather than publicizing the game.
// Check every named private field, including inherited BlendedAnimComponent data.
var controlFields = new (string Type, string Name, string FieldType)[]
{
    ("Assets.Scripts.Objects.Pipes.Dial", "_needleTransform", "UnityEngine.Transform"),
    ("Assets.Scripts.Objects.Pipes.Dial", "needle", "UnityEngine.GameObject"),
    ("Assets.Scripts.Objects.Pipes.Knob", "_knobTransform", "UnityEngine.Transform"),
    ("Assets.Scripts.Objects.Electrical.PowerGeneratorPipe", "_needleTransform", "UnityEngine.Transform"),
    ("Assets.Scripts.Objects.Pipes.FurnaceBase", "_needleTransform", "UnityEngine.Transform"),
    ("Assets.Scripts.Objects.Pipes.PipeGasMeter", "_needleTransform", "UnityEngine.Transform"),
    ("Assets.Scripts.Objects.Electrical.Transformer", "_needleTransform", "UnityEngine.Transform"),
    ("Assets.Scripts.Objects.Pipes.ChuteFlipFlopSplitter", "_needle", "UnityEngine.Transform"),
    ("Objects.BlendedAnimComponent", "animatedTransform", "UnityEngine.Transform"),
    ("Objects.SwitchOnOff", "switchTransform", "UnityEngine.Transform"),
    ("ActivateButton", "buttonTransform", "UnityEngine.Transform"),
    ("BinaryTransformAnimComponent", "state0", "AnimKeyFrameCollection"),
    ("BinaryTransformAnimComponent", "state1", "AnimKeyFrameCollection"),
    ("MultiStateAnimComponent", "states", "AnimKeyFrameCollection[]"),
    ("Assets.Scripts.Objects.Pipes.IndustrialBurner", "temperatureGaugeTransform", "UnityEngine.Transform"),
};
foreach (var field in controlFields)
    Require(game.MainModule.GetType(field.Type).Fields.Any(f => f.Name == field.Name && f.FieldType.FullName == field.FieldType),
        $"Control adapter field mismatch: {field.Type}.{field.Name}");
Console.WriteLine($"PASS {controlFields.Length} control adapter field names and types.");
var controlCapture = mod.MainModule.Types.Single(t => t.Name == "MirrorDrivenControls").Methods.Single(m => m.Name == "Discover");
foreach (string type in new[] { "Assets.Scripts.Objects.Pipes.PipeGasMeter", "Assets.Scripts.Objects.Electrical.Transformer",
    "Assets.Scripts.Objects.Pipes.ChuteFlipFlopSplitter" })
    Require(controlCapture.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Isinst &&
        i.Operand is TypeReference reference && reference.FullName == type), $"Missing re-enabled control adapter: {type}");
Console.WriteLine("PASS gas meter, transformer and chute splitter control adapters present.");
foreach (var spec in new[] { (Patch: "AssignedReferencePatch", Hook: "Prefix", Call: "Apply"),
    (Patch: "DeserializeJoinPatch", Hook: "Postfix", Call: "SetRegisteredMirror") })
{
    var hook = mod.MainModule.Types.Single(t => t.Name == spec.Patch).Methods.Single(m => m.Name == spec.Hook);
    var call = hook.Body.Instructions.Single(i => i.Operand is MethodReference method && method.Name == spec.Call);
    var handler = hook.Body.ExceptionHandlers.Single(h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Catch &&
        h.TryStart.Offset <= call.Offset && h.TryEnd.Offset > call.Offset);
    Require(handler.CatchType.FullName == "System.Exception", $"{spec.Patch}: mirror failures are not isolated");
    if (spec.Patch == "DeserializeJoinPatch")
    {
        var read = hook.Body.Instructions.Single(i => i.Operand is MethodReference method && method.Name == "ReadByte");
        Require(read.Offset < handler.TryStart.Offset, "Join byte must be consumed before mirror try/catch");
    }
}
Console.WriteLine("PASS load/create and join exception guards; join byte read before guarded mirror code.");
VariantCarrierChecks.Run(mod.MainModule);
AxisGeometryChecks.Run(mod.MainModule);
ControlMathChecks.Run();
AnimatorControlChecks.Run();
SelectionBoundsChecks.Run();
var animatorControls = mod.MainModule.Types.Single(t => t.Name == "MirrorAnimatorControls");
foreach (string controlType in new[] { "MirrorDrivenControls", "MirrorAnimatorControls" })
{
    var calls = mod.MainModule.Types.Single(t => t.Name == controlType).Methods.Where(m => m.HasBody)
        .SelectMany(m => m.Body.Instructions).Where(i => i.Operand is MethodReference)
        .Select(i => (MethodReference)i.Operand).ToArray();
    Require(calls.Any(m => m.DeclaringType.Name == "ControlTransformMath" && m.Name == "TrySolve") &&
        calls.Any(m => m.DeclaringType.Name == "MirrorControlGeometry" && m.Name == "TryRest"),
        controlType + " must use shared rest evidence and exact solve");
    Require(!calls.Any(m => m.Name is "FromToRotation" or "TryMotionAxis"), "Shaft-based control correction remains");
}
var selection = mod.MainModule.Types.Single(t => t.Name == "InteractableSelectionPatch").Methods.Single(m => m.Name == "Postfix").Body.Instructions;
Require(selection.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Isinst && i.Operand is TypeReference t && t.Name == "Structure") &&
    selection.Any(i => i.Operand is MethodReference m && m.Name == "IsMirrored") &&
    selection.Any(i => i.Operand is MethodReference m && m.Name == "RestoreVanillaExtent"),
    "Selection must scope shared extent correction to mirrored structures");
Require(!animatorControls.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Any(i => i.Operand is MethodReference method && method.Name is "SetParent" or "Rebind"),
    "Animator correction must not reparent or rebind");
var plugin = mod.MainModule.Types.Single(t => t.Name == "Plugin");
Require((int)plugin.CustomAttributes.Single(a => a.AttributeType.FullName == "UnityEngine.DefaultExecutionOrder")
    .ConstructorArguments[0].Value == 32000, "Late execution order missing");
var lateCalls = plugin.Methods.Single(m => m.Name == "LateUpdate").Body.Instructions
    .Where(i => i.Operand is MethodReference m && m.Name == "Tick")
    .Select(i => ((MethodReference)i.Operand).DeclaringType.Name).ToArray();
Require(lateCalls.SequenceEqual(new[] { "MirrorAnimatorControls", "MirrorProbe" }), "Correction must run in LateUpdate before the probe");
Console.WriteLine("PASS Animator no reparent/rebind calls and late correction before probe.");
Console.WriteLine("Offline metadata validation only; Harmony installation, Unity rendering and gameplay are untested.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}
