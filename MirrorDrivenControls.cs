using System.Collections;
using System.Reflection;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using Objects;
using Objects.RoboticArm;
using TMPro;
using UnityEngine;

namespace StationeersMirror;

internal sealed class DrivenControl
{
    internal Transform Node = null!;
    internal Transform Parent = null!;
    internal TransformPose Pose;
    internal int Sibling;
    internal ControlPose ProxyPose;
    internal System.Numerics.Matrix4x4 AroundRest;
    internal GameObject? Proxy;
}

internal sealed class ControlPart
{
    internal Transform Node = null!;
    internal string Source = "";
    internal string? Reason;
}

internal static class MirrorDrivenControls
{
    private static readonly Dictionary<Type, FieldInfo[]> Fields = new();

    internal static List<ControlPart> Discover(Structure structure)
    {
        var candidates = new Dictionary<Transform, ControlPart>();
        string componentSource = "";
        void Add(Transform? node, string source, string? reason = null)
        {
            if (node != null && node != structure.ThingTransform && node.IsChildOf(structure.ThingTransform))
            {
                source = componentSource + ": " + source;
                if (candidates.TryGetValue(node, out var previous))
                {
                    previous.Source += "; " + source;
                    previous.Reason = previous.Reason ?? reason;
                }
                else
                    candidates.Add(node, new ControlPart { Node = node, Source = source, Reason = reason });
            }
        }
        void Plain(object? value)
        {
            switch (value)
            {
                case Dial dial:
                    var needleNode = Read<Transform>(dial, "_needleTransform");
                    Add(needleNode ? needleNode : TransformOf(Read<GameObject>(dial, "needle")), "Dial._needleTransform");
                    break;
                case Knob knob:
                    var node = Read<Transform>(knob, "_knobTransform");
                    if (!node) node = TransformOf(knob.KnobGameObject);
                    Add(node, "Knob._knobTransform");
                    break;
                case NeedleRotator needle:
                    Add(needle.transform, "NeedleRotator.transform");
                    break;
            }
        }

        foreach (var component in structure.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (component == null || component.GetComponentInParent<Thing>() != structure)
                continue;
            componentSource = component.GetType().Name + "@" + Path(structure.ThingTransform, component.transform);
            MirrorControlDiscovery.Visit(component, Add);
            switch (component)
            {
                case SettingWheel wheel:
                    Add(wheel.Wheel, "SettingWheel.Wheel");
                    break;
                case Stacker stacker:
                    Add(stacker.Wheel, "Stacker.Wheel");
                    break;
                case VendingMachineRefrigerated vending:
                    Add(vending.Wheel, "VendingMachineRefrigerated.Wheel");
                    break;
                case PowerGeneratorPipe generator:
                    Add(Read<Transform>(generator, "_needleTransform"), "PowerGeneratorPipe._needleTransform");
                    break;
                case FurnaceBase furnace:
                    Add(Read<Transform>(furnace, "_needleTransform"), "FurnaceBase._needleTransform");
                    break;
                case PipeGasMeter meter:
                    Add(Read<Transform>(meter, "_needleTransform"), "PipeGasMeter._needleTransform");
                    break;
                case Transformer transformer:
                    Add(Read<Transform>(transformer, "_needleTransform"), "Transformer._needleTransform");
                    break;
                case ChuteFlipFlopSplitter splitter:
                    Add(Read<Transform>(splitter, "_needle"), "ChuteFlipFlopSplitter._needle");
                    break;
            }
            // Dial/Knob are plain serialized objects, NeedleRotator is a struct.
            // Inspect fields on every base class, including typed arrays/lists, but
            // do not traverse arbitrary object graphs or other Things.
            foreach (var field in ControlFields(component.GetType()))
            {
                var value = field.GetValue(component);
                if (value is IEnumerable sequence)
                    foreach (var item in sequence) Plain(item);
                else
                    Plain(value);
            }
        }
        return candidates.Values.ToList();
    }

    internal static List<DrivenControl> Capture(Structure structure, List<Connection> connections)
    {
        var result = new List<DrivenControl>();
        var discovered = Discover(structure);
        foreach (var candidate in discovered.OrderBy(part => Depth(part.Node)))
        {
            var node = candidate.Node;
            string? reason = candidate.Reason ?? UnsafeReason(node, structure.ThingTransform);
            if (reason == null && connections.Any(c => c.Transform &&
                (c.Transform == node || c.Transform.IsChildOf(node) || node.IsChildOf(c.Transform))))
                reason = "connection subtree has independent pose correction";
            if (reason != null)
            {
                Skip(structure, candidate.Source, reason);
                continue;
            }
            if (discovered.Any(c => c.Node != node && node.IsChildOf(c.Node)))
            {
                Skip(structure, candidate.Source, "script-driven ancestor: rest-pivot rule generally needs a time-varying proxy");
                continue;
            }
            var parent = node.parent;
            if (!MirrorControlGeometry.TryRest(structure, node, out var aroundRest, out var evidence))
            { Skip(structure, candidate.Source, evidence); continue; }
            var p = MirrorControlGeometry.ToRoot(parent, structure.ThingTransform);
            if ((MirrorControlGeometry.Read(node).Matrix * p).GetDeterminant() <= 0f ||
                !ControlTransformMath.TrySolve(p, p * ControlTransformMath.Mirror(MirrorState.GetVariant(structure)), aroundRest, out var g) ||
                !ControlTransformMath.TryTrs(g, out var proxyPose))
            { Skip(structure, candidate.Source, "vanilla pose is improper/singular or proxy requires shear"); continue; }
            if (Plugin.DebugEnabled)
                Plugin.Log?.LogInfo($"Mirror control {candidate.Source}: {evidence}; constant rest-pivot proxy.");
            result.Add(new DrivenControl
            {
                Node = node, Parent = parent, Pose = new TransformPose(node), Sibling = node.GetSiblingIndex(),
                ProxyPose = proxyPose, AroundRest = aroundRest
            });
        }
        return result;
    }

    internal static void Install(IEnumerable<DrivenControl> controls)
    {
        foreach (var control in controls)
        {
            var proxy = new GameObject("StationeersMirror.Control") { layer = control.Node.gameObject.layer };
            control.Proxy = proxy; // Journal before any operation that can throw.
            var transform = proxy.transform;
            transform.SetParent(control.Parent, false);
            transform.SetSiblingIndex(control.Sibling);
            MirrorControlGeometry.Write(transform, control.ProxyPose);
            control.Node.SetParent(transform, false);
            control.Pose.Restore(control.Node);
        }
    }

    internal static void Restore(IEnumerable<DrivenControl> controls)
    {
        var recovery = new MirrorRecovery();
        foreach (var control in controls.Reverse())
        {
            if (control.Node != null)
                recovery.Try(() =>
                {
                    control.Node.SetParent(control.Parent, false);
                    control.Node.SetSiblingIndex(control.Sibling);
                    control.Pose.Restore(control.Node);
                });
            if (control.Proxy != null)
                recovery.Try(() =>
                {
                    // Do not destroy a child if reparenting failed during recovery.
                    if (control.Proxy.transform.childCount == 0)
                    {
                        control.Proxy.SetActive(false);
                        control.Proxy.transform.SetParent(null, false);
                        UnityEngine.Object.Destroy(control.Proxy);
                    }
                });
        }
        recovery.ThrowIfFailed();
    }

    internal static bool Contains(IEnumerable<DrivenControl> controls, Transform node) =>
        controls.Any(c => node == c.Node || node.IsChildOf(c.Node));

    private static string? UnsafeReason(Transform node, Transform root)
    {
        if (MirrorOccupants.IsInsideOccupant(node, root))
            return "slot occupant is compensated as a whole";
        // Discovery selects explicit script-held Transform targets only. A root
        // Animator can drive a different mechanism (e.g. the motherboard flap);
        // its presence alone is not evidence that it drives this control.
        // Unknown/Animator-only targets are never candidates for reparenting.
        for (var current = node.parent; current != null; current = current.parent)
        {
            if (current != root && (current.GetComponent<Canvas>() != null ||
                current.GetComponent<TMP_Text>() != null || current.GetComponent<TextMesh>() != null))
                return "text subtree already supplies counter-reflection";
        }
        return null;
    }

    private static int Depth(Transform node)
    {
        int depth = 0;
        for (var parent = node.parent; parent != null; parent = parent.parent) depth++;
        return depth;
    }

    private static bool IsControl(Type type) => type == typeof(Dial) || type == typeof(Knob) || type == typeof(NeedleRotator);

    private static FieldInfo[] ControlFields(Type type)
    {
        if (Fields.TryGetValue(type, out var fields)) return fields;
        var result = new List<FieldInfo>();
        for (Type? current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
            result.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(f => IsControl(f.FieldType) || f.FieldType.IsArray && IsControl(f.FieldType.GetElementType()!) ||
                    f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>) && IsControl(f.FieldType.GetGenericArguments()[0])));
        Fields[type] = result.ToArray();
        return Fields[type];
    }

    // Unity-null safe replacements for ?. and ?? on Unity objects.
    private static Transform? TransformOf(GameObject? go) => go ? go!.transform : null;

    internal static T Read<T>(object owner, string name) => (T)AccessTools.Field(owner.GetType(), name).GetValue(owner);

    private static void Skip(Structure structure, string source, string reason)
    {
        if (Plugin.DebugEnabled)
            Plugin.Log?.LogInfo($"Mirror control left as mirror image: {structure.GetType().Name}/{source}: {reason}.");
    }

    internal static string Path(Transform root, Transform node)
    {
        var parts = new List<string>();
        for (var current = node; current && current != root; current = current.parent)
            parts.Add(current.name + "[" + current.GetSiblingIndex() + "]");
        parts.Reverse();
        return parts.Count == 0 ? "(root)" : string.Join("/", parts);
    }
}
