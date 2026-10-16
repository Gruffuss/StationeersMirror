using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class VariantCarrierChecks
{
    internal static void Run(ModuleDefinition mod)
    {
        TypeDefinition Type(string name) => mod.Types.Single(t => t.Name == name);
        MethodDefinition Method(string type, string name) => Type(type).Methods.Single(m => m.Name == name);
        var state = Type("MirrorState");
        foreach (string field in new[] { "Pending", "ClientBuildMirrored", "CursorVariant" })
            Require(state.Fields.Single(f => f.Name == field).FieldType.MetadataType == MetadataType.Byte, field + " must carry a byte");
        Require(Type("MirrorRecord").Fields.Single(f => f.Name == "Variant").FieldType.MetadataType == MetadataType.Byte, "record variant byte");
        Require(Method("MirrorState", "Remember").Parameters[1].ParameterType.MetadataType == MetadataType.Byte, "Remember byte");
        foreach (string method in new[] { "Read", "GetVariant" })
            Require(Method("MirrorState", method).ReturnType.MetadataType == MetadataType.Byte, method + " returns byte");
        Require(Method("MirrorState", "TryRead").Parameters[1].ParameterType.FullName == "System.Byte&", "TryRead out byte");
        Require(Call(Method("MirrorState", "Remember"), "Normalize") != null, "normalize carried values");

        foreach (string path in new[] { "CreateMessage", "ConstructionMessage", "Join" })
        {
            var write = Method("Serialize" + path + "Patch", "Postfix");
            var read = Method("Deserialize" + path + "Patch", "Postfix");
            var writer = WireCall(write, "RocketBinaryWriter", "WriteByte");
            var reader = WireCall(read, "RocketBinaryReader", "ReadByte");
            var writeMethod = (MethodReference)writer.Operand;
            var readMethod = (MethodReference)reader.Operand;
            Require(writeMethod.Parameters.Count == 1 && writeMethod.Parameters[0].ParameterType.MetadataType == MetadataType.Byte &&
                readMethod.ReturnType.MetadataType == MetadataType.Byte, path + " symmetric byte types");
            Require(writeMethod.Resolve() != null && readMethod.Resolve() != null, path + " installed game byte APIs");
            Require(writer.Previous.Operand is MethodReference source && source.DeclaringType.Name == "MirrorState" &&
                source.Name == (path == "Join" ? "GetVariant" : "Read"), path + " writes the variant without bool conversion");
            if (path == "Join")
            {
                CheckStructureGuard(write, writer);
                CheckStructureGuard(read, reader);
                var apply = Call(read, "SetRegisteredMirror");
                Require(((MethodReference)apply.Operand).Parameters[1].ParameterType.MetadataType == MetadataType.Byte, "join applies a byte");
                Require(Local(reader.Next) == Local(apply.Previous), "join passes the consumed byte unchanged");
            }
            else
            {
                Require(reader.Next.Operand is MethodReference sink && sink.Name == "Remember" &&
                    sink.Parameters[1].ParameterType.MetadataType == MetadataType.Byte, path + " reads directly into variant table");
                var process = Method("Process" + path + "Patch", "Prefix");
                var pending = Store(process, "Pending");
                Require(pending.Previous == Call(process, "Read"), path + " Process carries table byte to Pending");
            }
        }

        var create = Call(Method("CreateMessagePatch", "Postfix"), "Remember");
        Require(IsField(create.Previous, Code.Ldsfld, "ClientBuildMirrored"), "client scope -> create message");
        var rebuild = Call(Method("RebuildInstancePatch", "Postfix"), "Remember");
        Require(rebuild.Previous.Operand is MethodReference r && r.Name == "GetVariant", "old structure -> rebuild byte");
        var spawn = Method("SpawnConstructPatch", "Prefix");
        var carry = Call(spawn, "TryRead");
        Require(Local(carry.Previous) == Local(Store(spawn, "Pending").Previous), "rebuild out byte -> Pending");
        var construction = Method("ConstructionMessagePatch", "Postfix");
        Require(Call(construction, "TryRead") != null && Call(construction, "Remember") != null &&
            construction.Body.Instructions.Any(i => IsField(i, Code.Ldsfld, "Pending")), "construction message uses rebuild or Pending");
        var build = Method("UsePrimaryCompletePatch", "Prefix");
        Require(build.Parameters.Single().ParameterType.FullName == "System.Byte&" &&
            Method("UsePrimaryCompletePatch", "Finalizer").Parameters.Single().ParameterType.MetadataType == MetadataType.Byte,
            "client scope saves/restores byte");
        var cursorVariant = Call(build, "GetVariant");
        Require(IsField(cursorVariant.Previous, Code.Ldsfld, "ConstructionCursor"), "build reads actual cursor variant");
        Require(Store(build, "Pending").Offset > cursorVariant.Offset && Store(build, "ClientBuildMirrored").Offset > cursorVariant.Offset,
            "host and client build carriers follow cursor variant");
        var assigned = Method("AssignedReferencePatch", "Prefix");
        var clear = Store(assigned, "Pending");
        Require(clear.Previous.OpCode.Code == Code.Ldc_I4_0 && clear.Offset < Call(assigned, "Apply").Offset, "consume Pending before guarded Apply");

        var save = Method("SaveMarkerPatch", "Postfix");
        var saveValue = save.Body.Instructions.Single(i => IsField(i, Code.Stfld, "State"));
        Require(saveValue.Previous == Call(save, "GetVariant"), "save State receives full variant, not constant 1");
        var marker = (string)state.Fields.Single(f => f.Name == "SaveMarker").Constant;
        Require(save.Body.Instructions.Any(i => Equals(i.Operand, marker)) && Call(save, "RemoveAll") != null,
            "save writes the existing marker and removes stale entries");
        var load = Method("LoadMarkerPatch", "Prefix");
        var loadedValue = load.Body.Instructions.Single(i => IsField(i, Code.Ldfld, "State"));
        var normalize = Call(load, "Normalize");
        Require(loadedValue.Offset < normalize.Offset && normalize.Next == Store(load, "Pending") &&
            !load.Body.Instructions.Any(i => i.OpCode.Code == Code.Conv_U1), "load validates int State before narrowing to Pending byte");
        Require(Type("LoadMarkerPatch").NestedTypes.SelectMany(t => t.Methods).Where(m => m.HasBody)
            .Any(m => m.Body.Instructions.Any(i => Equals(i.Operand, marker)) &&
                m.Body.Instructions.Any(i => IsField(i, Code.Ldfld, "StateName"))), "load selects the same marker name");

        var choose = Method("CursorMirror", "AxisForViewMirror");
        Require(choose.ReturnType.MetadataType == MetadataType.Byte && Call(choose, "NearestAxis") != null,
            "cursor selects local mirror axis");
        Require(!mod.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is MethodReference m && (m.DeclaringType.FullName == "UnityEngine.Transform" && m.Name == "Rotate" || m.Name == "set_CurrentRotation") ||
                i.OpCode.Code == Code.Stsfld && i.Operand is FieldReference f && f.Name == "CurrentRotation"), "no Transform.Rotate or CurrentRotation writes anywhere in mod");
        Require(!Type("CursorMirror").Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is MethodReference m && m.Name is "set_rotation" or "set_localRotation"), "toggle does not assign rotations");
        var sync = Method("CursorMirror", "Synchronize");
        Require(sync.Body.Instructions.Any(i => IsField(i, Code.Ldsfld, "CursorVariant")) &&
            sync.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "GetVariant") &&
            !sync.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "AxisForViewMirror"), "cursor reuse preserves axis without a new camera choice");
        var rest = Method("MirrorControlGeometry", "TryRest");
        Require(Call(rest, "GetVariant").Offset < Call(rest, "AroundRest").Offset,
            "shared control rest solver uses stored axis");
        Require(!mod.Types.SelectMany(t => t.Methods).Any(m => m.Name is "ChooseExtraTurn" or "MeshOffset"), "mesh-offset inference removed");
        var applyBody = Method("MirrorUtil", "Apply");
        Require(Store(applyBody, "Variant").Offset < Call(applyBody, "Capture", "MirrorDrivenControls").Offset &&
            Store(applyBody, "Variant").Offset < Call(applyBody, "Capture", "MirrorAnimatorControls").Offset, "publish variant before either control capture");
        Console.WriteLine("PASS variant IL: three symmetric byte wire paths and Structure guards; host/client/rebuild carriers; save/load marker wiring; no cursor rotation; axis reuse and shared rest solver.");
    }

    private static Instruction WireCall(MethodDefinition method, string type, string name)
    {
        var calls = method.Body.Instructions.Where(i => i.Operand is MethodReference m && m.DeclaringType.Name == type).ToArray();
        Require(calls.Length == 1 && ((MethodReference)calls[0].Operand).Name == name, method.FullName + " exactly one " + name);
        return calls[0];
    }
    private static void CheckStructureGuard(MethodDefinition method, Instruction wire)
    {
        var test = method.Body.Instructions.Single(i => i.OpCode.Code == Code.Isinst);
        Require(((TypeReference)test.Operand).FullName == "Assets.Scripts.Objects.Structure", "join Structure guard");
        var branch = method.Body.Instructions.First(i => i.Offset > test.Offset && i.OpCode.FlowControl == FlowControl.Cond_Branch);
        Require(branch.OpCode.Code is Code.Brfalse or Code.Brfalse_S && branch.Offset < wire.Offset &&
            branch.Operand is Instruction target && target.Offset > wire.Offset, "non-Structures skip appended byte");
    }
    private static Instruction Call(MethodDefinition method, string name, string? type = null) =>
        method.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == name && (type == null || m.DeclaringType.Name == type));
    private static Instruction Store(MethodDefinition method, string field) =>
        method.Body.Instructions.Single(i => (i.OpCode.Code is Code.Stsfld or Code.Stfld) && i.Operand is FieldReference f && f.Name == field);
    private static bool IsField(Instruction instruction, Code code, string field) =>
        instruction.OpCode.Code == code && instruction.Operand is FieldReference f && f.Name == field;
    private static int Local(Instruction instruction) => instruction.OpCode.Code switch
    {
        Code.Ldloc_0 or Code.Stloc_0 => 0,
        Code.Ldloc_1 or Code.Stloc_1 => 1,
        Code.Ldloc_2 or Code.Stloc_2 => 2,
        Code.Ldloc_3 or Code.Stloc_3 => 3,
        Code.Ldloc or Code.Ldloc_S or Code.Stloc or Code.Stloc_S or Code.Ldloca or Code.Ldloca_S => ((VariableDefinition)instruction.Operand).Index,
        _ => throw new Exception("Expected a local variable: " + instruction)
    };
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception("Variant carrier check: " + message); }
}
