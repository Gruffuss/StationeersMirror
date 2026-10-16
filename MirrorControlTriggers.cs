using Assets.Scripts.Objects;
using UnityEngine;
using N = System.Numerics;

namespace StationeersMirror;

// The regulator's increase/decrease boxes (BoxColliderButton1Trigger001/BoxColliderButton2Trigger001)
// are plain root children. They follow the wheel's rest-pivot rule for the stored
// mirror axis, with E always identity and no change to the structure's rotation.
// An interaction collider whose centre lies inside a control's own mesh bounds now moves
// rigidly with that control: the same rule D with the control's rest pivot and mirror axis, so it
// keeps its vanilla offset from the control. Such a box also gets a proper transform, so
// Unity's negative-scale BoxCollider warning goes away for it.
internal static class MirrorControlTriggers
{
    internal readonly struct Group
    {
        internal readonly Transform Node;
        internal readonly N.Matrix4x4 AroundRest;
        internal Group(Transform node, N.Matrix4x4 aroundRest) { Node = node; AroundRest = aroundRest; }
    }

    // Call before the root flip and before proxies are installed: poses are still vanilla.
    internal static List<(Transform Node, ControlPose Pose)> Plan(Structure owner, IEnumerable<Group> groups,
        ICollection<Transform> excluded)
    {
        var root = owner.ThingTransform;
        var boxes = groups.Where(g => g.Node)
            .Select(g => (Group: g, Box: RootBounds(root, g.Node)))
            .Where(x => x.Box.HasValue).ToArray();
        var result = new List<(Transform, ControlPose)>();
        if (boxes.Length == 0) return result;
        foreach (var interactable in owner.Interactables)
        {
            var collider = interactable.Collider;
            if (!collider) continue;
            var t = collider.transform;
            if (t == root || excluded.Contains(t) || result.Any(r => r.Item1 == t)) continue;
            if (boxes.Any(b => t == b.Group.Node || t.IsChildOf(b.Group.Node))) continue; // already moves with its control
            var centre = root.InverseTransformPoint(t.TransformPoint(LocalCentre(collider)));
            var owners = boxes.Where(b => b.Box!.Value.Contains(centre))
                .OrderBy(b => b.Box!.Value.size.sqrMagnitude).ToArray();
            if (owners.Length == 0) continue;
            var group = owners[0].Group;
            var p = MirrorControlGeometry.ToRoot(t.parent, root);
            if (!ControlTransformMath.TrySolve(p, p * ControlTransformMath.Mirror(MirrorState.GetVariant(owner)), group.AroundRest, out var g) ||
                !ControlTransformMath.TryTrs(MirrorControlGeometry.Read(t).Matrix * g, out var pose))
            {
                if (Plugin.DebugEnabled)
                    Plugin.Log?.LogInfo($"Mirror trigger {interactable.Action} {MirrorDrivenControls.Path(root, t)}: " +
                        "no exact pose with its control; left mirrored.");
                continue;
            }
            if (Plugin.DebugEnabled)
                Plugin.Log?.LogInfo($"Mirror trigger {interactable.Action} {MirrorDrivenControls.Path(root, t)} " +
                    $"moves with control {MirrorDrivenControls.Path(root, group.Node)}.");
            result.Add((t, pose));
        }
        return result;
    }

    private static Vector3 LocalCentre(Collider collider) => collider switch
    {
        BoxCollider box => box.center,
        SphereCollider sphere => sphere.center,
        CapsuleCollider capsule => capsule.center,
        MeshCollider mesh when mesh.sharedMesh => mesh.sharedMesh.bounds.center,
        _ => Vector3.zero
    };

    // The control's mesh bounds in root space, padded by 10% (at least 5 mm) per side.
    private static Bounds? RootBounds(Transform root, Transform node)
    {
        Bounds? total = null;
        foreach (var filter in node.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter || !filter.sharedMesh) continue;
            var b = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var point = root.InverseTransformPoint(filter.transform.TransformPoint(corner));
                if (total == null) total = new Bounds(point, Vector3.zero);
                else { var grown = total.Value; grown.Encapsulate(point); total = grown; }
            }
        }
        if (total == null) return null;
        var padded = total.Value;
        var pad = Vector3.Max(padded.extents * 0.1f, Vector3.one * 0.005f);
        padded.extents += pad;
        return padded;
    }
}
