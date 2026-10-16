using Assets.Scripts.Objects;
using UnityEngine;
using N = System.Numerics;

namespace StationeersMirror;

internal static class MirrorControlGeometry
{
    internal static N.Vector3 Vector(Vector3 v) => new(v.x, v.y, v.z);
    internal static Vector3 Vector(N.Vector3 v) => new(v.X, v.Y, v.Z);
    internal static ControlPose Read(Transform node) => new(Vector(node.localPosition),
        new N.Quaternion(node.localRotation.x, node.localRotation.y, node.localRotation.z, node.localRotation.w),
        Vector(node.localScale));
    internal static void Write(Transform node, ControlPose pose)
    {
        var p = Vector(pose.Position);
        var q = new Quaternion(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W);
        var s = Vector(pose.Scale);
        if (!node.localPosition.Equals(p)) node.localPosition = p;
        if (!node.localRotation.Equals(q)) node.localRotation = q;
        if (!node.localScale.Equals(s)) node.localScale = s;
    }
    internal static N.Matrix4x4 ToRoot(Transform node, Transform root, Func<Transform, ControlPose>? read = null)
    {
        var result = N.Matrix4x4.Identity;
        for (var current = node; current && current != root; current = current.parent)
            result *= (read == null ? Read(current) : read(current)).Matrix;
        return result;
    }

    // Match each path segment uniquely, not by sibling index: cursor creation
    // removes UI children. Never guess between duplicate prefab sibling names.
    internal static Transform? FindRest(Structure owner, Transform node)
    {
        var prefab = owner.SourcePrefab;
        if (!prefab) return null;
        var names = new Stack<string>();
        for (var current = node; current && current != owner.ThingTransform; current = current.parent)
            names.Push(current.name);
        var rest = prefab.ThingTransform;
        foreach (var name in names)
        {
            Transform? match = null;
            for (int i = 0; i < rest.childCount; i++)
            {
                var child = rest.GetChild(i);
                if (child.name != name) continue;
                if (match) return null;
                match = child;
            }
            if (!match) return null;
            rest = match!;
        }
        return rest;
    }

    internal static bool TryRest(Structure owner, Transform node, out N.Matrix4x4 aroundRest, out string evidence)
    {
        aroundRest = default;
        var rest = FindRest(owner, node);
        if (!rest) { evidence = "missing/ambiguous prefab rest path"; return false; }
        var root = owner.SourcePrefab.ThingTransform;
        byte variant = MirrorState.GetVariant(owner);
        var p0 = N.Vector3.Transform(N.Vector3.Zero, ToRoot(rest!, root));
        // Mirror along the control's panel: keep its vanilla look (E = identity). Mirror
        // through its panel (axis = facing): the control lands on the opposite face, so also
        // turn it 180 degrees about the structure axis nearest world up, which faces it outward
        // and keeps the vanilla look seen from the front. Facing = the control mesh's thinnest
        // axis, confirmed by its position against the structure bounds (both matched every
        // control measured in game, 2026-10-16; the click box did not, on the regulator).
        int facing = FacingAxis(owner.SourcePrefab, root, rest!, (Vector3)ToUnity(p0), out string facingNote);
        byte turn = 0;
        if (facing < 0)
        {
            evidence = $"mirror axis={MirrorVariant.AxisName(variant)}; facing unclear ({facingNote}); left as plain mirror";
            return false;
        }
        if (facing == variant - 1)
            turn = UpAxis(owner.ThingTransform, variant);
        evidence = $"mirror axis={MirrorVariant.AxisName(variant)}; facing={"XYZ"[facing]} ({facingNote}); " +
            (turn == 0 ? "E=identity" : $"E=half-turn about {"XYZ"[turn - 1]} (mirror runs through the panel)");
        aroundRest = ControlTransformMath.AroundRest(p0, variant, turn);
        return true;
    }

    private static Vector3 ToUnity(N.Vector3 v) => new(v.X, v.Y, v.Z);

    // Root axis index (0..2) the control faces along, or -1 when the two measures disagree.
    private static int FacingAxis(Thing prefab, Transform root, Transform rest, Vector3 pivot, out string note)
    {
        int thin = -1;
        float bestVolume = -1f;
        foreach (var filter in rest.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter || !filter.sharedMesh) continue;
            var size = filter.sharedMesh.bounds.size;
            var t = filter.transform;
            var axes = new[]
            {
                root.InverseTransformVector(t.TransformVector(new Vector3(size.x, 0, 0))),
                root.InverseTransformVector(t.TransformVector(new Vector3(0, size.y, 0))),
                root.InverseTransformVector(t.TransformVector(new Vector3(0, 0, size.z)))
            };
            float volume = axes[0].magnitude * axes[1].magnitude * axes[2].magnitude;
            if (volume <= bestVolume) continue;
            bestVolume = volume;
            var thinnest = axes.OrderBy(a => a.magnitude).First().normalized;
            thin = Dominant(thinnest, out float strength);
            if (strength < 0.9f) thin = -1;
        }
        var bounds = prefab.Bounds;
        var rel = pivot - bounds.center;
        var relative = new Vector3(Ratio(rel.x, bounds.extents.x), Ratio(rel.y, bounds.extents.y), Ratio(rel.z, bounds.extents.z));
        int placed = Dominant(relative, out _);
        note = $"mesh thinnest={(thin < 0 ? "?" : "XYZ"[thin].ToString())}, position={"XYZ"[placed]}";
        return thin >= 0 && thin == placed ? thin : -1;
    }

    private static float Ratio(float value, float extent) => extent > 1e-5f ? value / extent : 0f;

    private static int Dominant(Vector3 v, out float strength)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z), sum = v.magnitude;
        int axis = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        strength = sum > 1e-9f ? new[] { ax, ay, az }[axis] / sum : 0f;
        return axis;
    }

    // 1..3: the structure axis other than the mirror axis that points most nearly up in the world.
    private static byte UpAxis(Transform root, byte variant)
    {
        var up = root.InverseTransformDirection(Vector3.up);
        float[] c = { Mathf.Abs(up.x), Mathf.Abs(up.y), Mathf.Abs(up.z) };
        c[variant - 1] = -1f;
        int best = c[0] >= c[1] && c[0] >= c[2] ? 0 : c[1] >= c[2] ? 1 : 2;
        return (byte)(best + 1);
    }
}
