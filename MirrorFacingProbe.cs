using Assets.Scripts.Objects;
using UnityEngine;

namespace StationeersMirror;

// Debug-only measurement (owner, 2026-10-16): after round 7, controls are right when the mirror
// axis lies along their panel (computer and Larre with axis X) but face into the device when it
// runs through the panel (both with axis Z): the control then needs a half-turn to face out,
// which requires knowing which way each control faces. The prefab data does not say. This logs
// four candidate measures of the facing, all in the vanilla prefab's root space, so the one
// matching the known cases (computer rocker and Larre button face along root Z) can be chosen.
internal static class MirrorFacingProbe
{
    private const string ProxyName = "StationeersMirror.Control";

    internal static void Log(Structure owner, IEnumerable<Transform> controls)
    {
        var prefab = owner.SourcePrefab;
        if (!prefab) return;
        var prefabRoot = prefab.ThingTransform;
        foreach (var node in controls)
        {
            if (!node) continue;
            var rest = FindRest(owner.ThingTransform, prefabRoot, node);
            if (!rest)
            {
                Plugin.Log?.LogInfo($"Mirror facing: {owner.PrefabName} {node.name}: no unique rest node in the prefab.");
                continue;
            }
            var pivot = prefabRoot.InverseTransformPoint(rest!.position);
            string trigger = TriggerEvidence(prefab, prefabRoot, rest);
            string meshBox = MeshBoxEvidence(prefabRoot, rest);
            string normals = NormalEvidence(prefabRoot, rest);
            var bounds = prefab.Bounds;
            var rel = pivot - bounds.center;
            var norm = new Vector3(Ratio(rel.x, bounds.extents.x), Ratio(rel.y, bounds.extents.y), Ratio(rel.z, bounds.extents.z));
            Plugin.Log?.LogInfo($"Mirror facing: {owner.PrefabName} {rest.name}: pivot={pivot.ToString("G4")}; " +
                $"(a) click box thinnest={trigger}; (b) mesh box thinnest={meshBox}; (c) area-weighted normal={normals}; " +
                $"(d) position vs structure bounds={Dominant(norm)} (relative {norm.ToString("G3")}, bounds centre {bounds.center.ToString("G3")} extents {bounds.extents.ToString("G3")}).");
        }
    }

    private static float Ratio(float value, float extent) => extent > 1e-5f ? value / extent : 0f;

    // The rest node in the vanilla prefab, by path, skipping this mod's proxy parents.
    private static Transform? FindRest(Transform root, Transform prefabRoot, Transform node)
    {
        var names = new Stack<string>();
        for (var t = node; t && t != root; t = t.parent)
            if (t.name != ProxyName) names.Push(t.name);
        var current = prefabRoot;
        foreach (var name in names)
        {
            Transform? match = null;
            for (int i = 0; i < current.childCount; i++)
            {
                var child = current.GetChild(i);
                if (child.name != name) continue;
                if (match) return null;
                match = child;
            }
            if (!match) return null;
            current = match!;
        }
        return current;
    }

    // Box colliders of the prefab's interactables that lie on or inside the control.
    private static string TriggerEvidence(Thing prefab, Transform prefabRoot, Transform rest)
    {
        var meshBounds = RootMeshBounds(prefabRoot, rest);
        var results = new List<string>();
        foreach (var interactable in prefab.Interactables)
        {
            if (!(interactable.Collider is BoxCollider box) || !box) continue;
            var t = box.transform;
            var centre = prefabRoot.InverseTransformPoint(t.TransformPoint(box.center));
            bool belongs = t == rest || t.IsChildOf(rest) || (meshBounds.HasValue && meshBounds.Value.Contains(centre));
            if (!belongs) continue;
            var axes = new[]
            {
                prefabRoot.InverseTransformVector(t.TransformVector(new Vector3(box.size.x, 0, 0))),
                prefabRoot.InverseTransformVector(t.TransformVector(new Vector3(0, box.size.y, 0))),
                prefabRoot.InverseTransformVector(t.TransformVector(new Vector3(0, 0, box.size.z)))
            };
            var thin = axes.OrderBy(a => a.magnitude).First();
            results.Add($"{interactable.Action}:{Dominant(thin.normalized)} (sizes {string.Join("/", axes.Select(a => a.magnitude.ToString("G3")))})");
        }
        return results.Count == 0 ? "none" : string.Join(", ", results);
    }

    private static string MeshBoxEvidence(Transform prefabRoot, Transform rest)
    {
        var results = new List<string>();
        foreach (var filter in rest.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter || !filter.sharedMesh) continue;
            var size = filter.sharedMesh.bounds.size;
            var t = filter.transform;
            var axes = new[]
            {
                prefabRoot.InverseTransformVector(t.TransformVector(new Vector3(size.x, 0, 0))),
                prefabRoot.InverseTransformVector(t.TransformVector(new Vector3(0, size.y, 0))),
                prefabRoot.InverseTransformVector(t.TransformVector(new Vector3(0, 0, size.z)))
            };
            var thin = axes.OrderBy(a => a.magnitude).First();
            results.Add($"{filter.sharedMesh.name}:{Dominant(thin.normalized)} (sizes {string.Join("/", axes.Select(a => a.magnitude.ToString("G3")))})");
        }
        return results.Count == 0 ? "none" : string.Join(", ", results);
    }

    // Sum of triangle area vectors: zero for a closed mesh, outward for a mesh with an open back.
    private static string NormalEvidence(Transform prefabRoot, Transform rest)
    {
        var results = new List<string>();
        foreach (var filter in rest.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter || !filter.sharedMesh) continue;
            var mesh = filter.sharedMesh;
            if (!mesh.isReadable) { results.Add($"{mesh.name}:not readable"); continue; }
            var v = mesh.vertices;
            var tris = mesh.triangles;
            var t = filter.transform;
            var sum = Vector3.zero;
            float total = 0f;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var a = prefabRoot.InverseTransformPoint(t.TransformPoint(v[tris[i]]));
                var b = prefabRoot.InverseTransformPoint(t.TransformPoint(v[tris[i + 1]]));
                var c = prefabRoot.InverseTransformPoint(t.TransformPoint(v[tris[i + 2]]));
                var area = Vector3.Cross(b - a, c - a) * 0.5f;
                sum += area;
                total += area.magnitude;
            }
            float strength = total > 1e-9f ? sum.magnitude / total : 0f;
            results.Add($"{mesh.name}:{Dominant(sum.normalized)} (strength {strength:G3})");
        }
        return results.Count == 0 ? "none" : string.Join(", ", results);
    }

    private static Bounds? RootMeshBounds(Transform prefabRoot, Transform rest)
    {
        Bounds? total = null;
        foreach (var filter in rest.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!filter || !filter.sharedMesh) continue;
            var b = filter.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var p = prefabRoot.InverseTransformPoint(filter.transform.TransformPoint(corner));
                if (total == null) total = new Bounds(p, Vector3.zero);
                else { var g = total.Value; g.Encapsulate(p); total = g; }
            }
        }
        if (total == null) return null;
        var padded = total.Value;
        padded.extents += Vector3.Max(padded.extents * 0.1f, Vector3.one * 0.005f);
        return padded;
    }

    private static string Dominant(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ax >= ay && ax >= az) return (v.x >= 0 ? "+X" : "-X") + $"({ax:G2})";
        if (ay >= az) return (v.y >= 0 ? "+Y" : "-Y") + $"({ay:G2})";
        return (v.z >= 0 ? "+Z" : "-Z") + $"({az:G2})";
    }
}
