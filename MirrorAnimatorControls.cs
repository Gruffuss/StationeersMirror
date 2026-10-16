using Assets.Scripts.Objects;
using TMPro;
using UnityEngine;
using N = System.Numerics;

namespace StationeersMirror;

internal sealed class AnimatorControl
{
    internal Transform Node = null!;
    internal Transform Parent = null!;
    internal N.Matrix4x4 AroundRest;
    internal readonly ControlPoseWriteback State = new();
    internal Vector3 LastScale;
    internal bool ScaleWarning, TrsWarning;
}

internal static class MirrorAnimatorControls
{
    private sealed class Watch
    {
        internal readonly WeakReference<Structure> Owner;
        internal readonly List<AnimatorControl> Controls;
        internal Watch(Structure owner, List<AnimatorControl> controls)
        { Owner = new WeakReference<Structure>(owner); Controls = controls; }
    }
    private static readonly List<Watch> Watches = new();

    internal static List<AnimatorControl> Capture(Structure owner, MirrorRecord record, List<Connection> connections)
    {
        var result = new List<AnimatorControl>();
        var root = owner.ThingTransform;
        foreach (var node in root.GetComponentsInChildren<Transform>(true))
        {
            if (!node || node == root || node is RectTransform || !AnimatorControlPolicy.IsControlName(node.name)) continue;
            if (record.DrivenControls.Any(c => c.Node &&
                (c.Node == node || node.IsChildOf(c.Node) || c.Node.IsChildOf(node)))) continue;
            if (MirrorOccupants.IsInsideOccupant(node, root)) continue;
            var rest = MirrorControlGeometry.FindRest(owner, node);
            var prefab = owner.SourcePrefab;
            bool animator = owner.IsCursor ? rest && prefab && HasAnimatorAncestor(rest!, prefab.ThingTransform) :
                HasAnimatorAncestor(node, root);
            if (!animator) continue;
            string? skip = null;
            if (owner.IsCursor && node.GetComponentsInParent<Animator>(true).Any(a => a && a.transform != root))
                skip = "cursor still has a nested Animator; a one-time correction would be overwritten";
            else if (InTextTree(node, root)) skip = "text/Canvas subtree already compensated";
            else if (connections.Any(c => c.Transform &&
                (c.Transform == node || c.Transform.IsChildOf(node) || node.IsChildOf(c.Transform))))
                skip = "connection subtree has independent pose correction";
            bool found = MirrorControlGeometry.TryRest(owner, node, out var aroundRest, out string evidence);
            if (skip == null && !found) skip = evidence;
            var p = MirrorControlGeometry.ToRoot(node.parent, root);
            if (skip == null && (!ControlTransformMath.TrySolve(p, p * ControlTransformMath.Mirror(record.Variant), aroundRest, out var g) ||
                !ControlTransformMath.TryTrs(MirrorControlGeometry.Read(node).Matrix * g, out _)))
                skip = "local result requires shear/singular TRS";
            if (Plugin.DebugEnabled)
                Plugin.Log?.LogInfo($"Mirror Animator heuristic: {owner.PrefabName} #{owner.ReferenceId} " +
                    $"{MirrorDrivenControls.Path(root, node)}: {skip ?? evidence + "; rest-pivot pose correction"}");
            if (skip == null) result.Add(new AnimatorControl
            { Node = node, Parent = node.parent, AroundRest = aroundRest, LastScale = node.localScale });
        }
        return result;
    }

    internal static void Install(Structure owner, List<AnimatorControl> controls)
    {
        Correct(owner, controls, false);
        Unregister(owner);
        if (!owner.IsCursor && controls.Count != 0) Watches.Add(new Watch(owner, controls));
    }

    internal static void Tick()
    {
        for (int i = Watches.Count - 1; i >= 0; i--)
        {
            var watch = Watches[i];
            if (!watch.Owner.TryGetTarget(out var owner) || !owner || owner.IsCursor || !MirrorState.IsMirrored(owner))
            { Watches.RemoveAt(i); continue; }
            Correct(owner, watch.Controls, true);
        }
    }

    private static void Correct(Structure owner, List<AnimatorControl> controls, bool detectScaleWrite)
    {
        var root = owner.ThingTransform;
        // Snapshot vanilla samples before any writes. Ancestor controls are read
        // through these samples, so nested corrections never feed back into V.
        var vanilla = new Dictionary<Transform, ControlPose>();
        foreach (var control in controls)
            if (control.Node) vanilla[control.Node] = control.State.Read(MirrorControlGeometry.Read(control.Node));
        ControlPose ReadVanilla(Transform node) => vanilla.TryGetValue(node, out var pose) ? pose : MirrorControlGeometry.Read(node);
        foreach (var control in controls.OrderBy(c => c.Node ? Depth(c.Node) : int.MaxValue))
        {
            var node = control.Node;
            if (!node) continue;
            if (node.parent != control.Parent || !node.IsChildOf(root))
            {
                Restore(control);
                if (Plugin.DebugEnabled) Plugin.Log?.LogInfo($"Mirror Animator control reparented: {node.name}; correction removed.");
                control.Node = null!;
                continue;
            }
            var raw = vanilla[node];
            var p = MirrorControlGeometry.ToRoot(node.parent, root, ReadVanilla);
            var actual = MirrorControlGeometry.ToRoot(node.parent, root) * ControlTransformMath.Mirror(MirrorState.GetVariant(owner));
            if (!ControlTransformMath.TrySolve(p, actual, control.AroundRest, out var g) ||
                (raw.Matrix * p).GetDeterminant() <= 0f || !ControlTransformMath.TryTrs(raw.Matrix * g, out var corrected))
            {
                Restore(control);
                if (Plugin.DebugEnabled && !control.TrsWarning)
                    Plugin.Log?.LogInfo($"Mirror Animator control {MirrorDrivenControls.Path(root, node)}: pose is improper/singular or needs shear; left mirrored for this frame.");
                control.TrsWarning = true;
                continue;
            }
            if (detectScaleWrite && !node.localScale.Equals(control.LastScale) && !control.ScaleWarning)
            {
                control.ScaleWarning = true;
                Plugin.Log?.LogWarning($"Mirror Animator control scale changed: {owner.PrefabName} #{owner.ReferenceId} " +
                    $"{MirrorDrivenControls.Path(root, node)}; recomputing exact TRS (logged once).");
            }
            MirrorControlGeometry.Write(node, corrected);
            control.State.Written(raw, MirrorControlGeometry.Read(node));
            control.LastScale = node.localScale;
        }
    }

    internal static void Restore(Structure owner, List<AnimatorControl> controls)
    {
        Unregister(owner);
        var recovery = new MirrorRecovery();
        foreach (var control in controls)
            if (control.Node) recovery.Try(() => Restore(control));
        recovery.ThrowIfFailed();
    }
    private static void Restore(AnimatorControl control) =>
        MirrorControlGeometry.Write(control.Node, control.State.Restore(MirrorControlGeometry.Read(control.Node)));
    private static void Unregister(Structure owner) => Watches.RemoveAll(w =>
        !w.Owner.TryGetTarget(out var current) || !current || current == owner);
    internal static bool Contains(IEnumerable<AnimatorControl> controls, Transform node) =>
        controls.Any(c => c.Node && (node == c.Node || node.IsChildOf(c.Node)));
    private static bool HasAnimatorAncestor(Transform node, Transform root)
    {
        for (var parent = node.parent; parent; parent = parent.parent)
        {
            if (parent.GetComponent<Animator>()) return true;
            if (parent == root) break;
        }
        return false;
    }
    private static int Depth(Transform node)
    {
        int depth = 0;
        for (var parent = node.parent; parent; parent = parent.parent) depth++;
        return depth;
    }
    private static bool InTextTree(Transform node, Transform root)
    {
        for (var current = node; current && current != root; current = current.parent)
            if (current.GetComponent<Canvas>() || current.GetComponent<TMP_Text>() || current.GetComponent<TextMesh>()) return true;
        return false;
    }
}
