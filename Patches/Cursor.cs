using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.UI;
using HarmonyLib;
using UnityEngine;

namespace StationeersMirror.Patches;

internal static class CursorMirror
{
    internal static void Synchronize()
    {
        var cursor = InventoryManager.ConstructionCursor;
        if (cursor == null)
            return;
        byte wanted = MirrorUtil.CanBuildMirrored(cursor, out _) ? MirrorState.CursorVariant : MirrorVariant.Off;
        if (MirrorState.GetVariant(cursor) == wanted)
            return;
        byte previous = MirrorState.GetVariant(cursor);
        // Runs inside the game's placement update: an exception here would abort
        // vanilla placement, so a failure turns the toggle off and is logged once.
        try
        {
            MirrorUtil.Apply(cursor, wanted);
        }
        catch (Exception error)
        {
            MirrorState.CursorVariant = MirrorVariant.Off;
            Plugin.Log?.LogError($"Mirror toggle failed for {cursor.PrefabName}, mirror turned off: {error}");
        }
        // Reconcile visuals once, even if Apply restored vanilla during recovery.
        try { SynchronizeWireframe(cursor.Wireframe, previous, MirrorState.GetVariant(cursor)); }
        catch (Exception error) { Plugin.Log?.LogError($"Mirror wireframe failed for {cursor.PrefabName}: {error}"); }
    }

    private static readonly AccessTools.FieldRef<Wireframe, Vector3> WireframeLastPosition =
        AccessTools.FieldRefAccess<Wireframe, Vector3>("_lastPosition");

    // The blueprint wireframe is GL lines computed from position + rotation only
    // (Wireframe.TransformPoint, Wireframe.cs:44-47), so the root scale never reaches it.
    // Reflect its own data instead; doing it twice restores it. Edge is
    // [Serializable], so each cursor's Instantiate (InventoryManager.cs:781) owns its list.
    private static void SynchronizeWireframe(Wireframe? wireframe, byte previous, byte wanted)
    {
        if (previous == wanted) return;
        ReflectWireframe(wireframe, previous); // restore the old axis first
        ReflectWireframe(wireframe, wanted);
        if (wireframe) SetBlueprintMirror(wireframe!, wanted);
    }

    private static void ReflectWireframe(Wireframe? wireframe, byte axis)
    {
        if (!wireframe || axis == MirrorVariant.Off)
            return;
        foreach (var edge in wireframe!.WireframeEdges)
        {
            edge.Point1 = MirrorUtil.Reflect(edge.Point1, axis);
            edge.Point2 = MirrorUtil.Reflect(edge.Point2, axis);
        }
        var bounds = wireframe.BlueprintBounds;
        wireframe.BlueprintBounds = new Bounds(MirrorUtil.Reflect(bounds.center, axis), bounds.size);
        wireframe.OrientationArrowOffset = MirrorUtil.Reflect(wireframe.OrientationArrowOffset, axis);
        // Lines are cached until position or rotation changes (Wireframe.cs:91); force a redraw.
        WireframeLastPosition(wireframe) = new Vector3(float.NaN, float.NaN, float.NaN);
    }

    // Mirrored source mesh -> its reflected copy; a cursor exists per kit, so this stays small.
    private static readonly Dictionary<(Mesh Source, byte Axis), Mesh> MirroredMeshes = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Wireframe, BlueprintState> Blueprints = new();
    private sealed class BlueprintState
    {
        internal MeshFilter Filter = null!;
        internal Mesh Original = null!;
        internal Vector3 Scale;
    }

    // Seen in game (owner, 2026-10-16): on the mirrored ghost every wireframe line, back and
    // interior ones included, draws over the green model; vanilla hides them. Unconfirmed
    // cause: the blueprint shader does not get Unity's culling inversion under the negative
    // root scale, so the far faces render and write depth. Fix that holds either way: give the
    // blueprint renderer a reflected mesh copy with reversed winding and cancel the reflection
    // on its own transform, so it renders with a positive determinant and identical geometry.
    // Restore the exact original before choosing another axis; a failed copy must
    // not accidentally flip an untouched blueprint when the cursor is turned off.
    private static void SetBlueprintMirror(Wireframe wireframe, byte axis)
    {
        if (Blueprints.TryGetValue(wireframe, out var previous))
        {
            if (previous.Filter)
            {
                previous.Filter.sharedMesh = previous.Original;
                previous.Filter.transform.localScale = previous.Scale;
            }
            Blueprints.Remove(wireframe);
        }
        if (axis == MirrorVariant.Off) return;
        var filter = wireframe.BlueprintMeshFilter;
        if (!filter || !filter.sharedMesh)
            return;
        var t = filter.transform;
        if (t.childCount != 0)
        {
            // Cancelling the reflection here would un-mirror the children too.
            Plugin.Log?.LogWarning($"Mirror: blueprint mesh {filter.sharedMesh.name} has child objects; left as is.");
            return;
        }
        var renderer = filter.GetComponent<Renderer>();
        if (Plugin.DebugEnabled && renderer)
        {
            var material = renderer.sharedMaterial;
            string shader = material && material.shader ? material.shader.name : "none";
            Plugin.Log?.LogInfo($"Mirror blueprint: mesh={filter.sharedMesh.name} shader={shader} " +
                $"det={renderer.localToWorldMatrix.determinant:F3} readable={filter.sharedMesh.isReadable}");
        }
        var current = filter.sharedMesh;
        Mesh next;
        try
        {
            var key = (current, axis);
            next = MirroredMeshes.TryGetValue(key, out var cached) && cached ? cached : (MirroredMeshes[key] = ReflectedCopy(current, axis));
        }
        catch (Exception error)
        {
            // Non-readable meshes cannot be copied on the CPU; keep the previous look.
            Plugin.Log?.LogWarning($"Mirror: could not copy blueprint mesh {current.name}: {error.Message}");
            return;
        }
        Blueprints.Add(wireframe, new BlueprintState { Filter = filter, Original = current, Scale = t.localScale });
        filter.sharedMesh = next;
        t.localScale = MirrorUtil.Reflect(t.localScale, axis);
    }

    private static Mesh ReflectedCopy(Mesh source, byte axis)
    {
        var mesh = UnityEngine.Object.Instantiate(source);
        try
        {
            mesh.name = source.name + " (mirrored " + MirrorVariant.AxisName(axis) + ")";
            mesh.vertices = mesh.vertices.Select(v => MirrorUtil.Reflect(v, axis)).ToArray();
            if (mesh.normals.Length > 0)
                mesh.normals = mesh.normals.Select(n => MirrorUtil.Reflect(n, axis)).ToArray();
            if (mesh.tangents.Length > 0)
                mesh.tangents = mesh.tangents.Select(tg =>
                {
                    var direction = MirrorUtil.Reflect(new Vector3(tg.x, tg.y, tg.z), axis);
                    return new Vector4(direction.x, direction.y, direction.z, -tg.w);
                }).ToArray();
            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                var triangles = mesh.GetTriangles(i);
                for (int j = 0; j < triangles.Length; j += 3)
                    (triangles[j + 1], triangles[j + 2]) = (triangles[j + 2], triangles[j + 1]);
                mesh.SetTriangles(triangles, i);
            }
            mesh.RecalculateBounds();
            return mesh;
        }
        catch
        {
            UnityEngine.Object.Destroy(mesh);
            throw;
        }
    }

    internal static void Toggle()
    {
        var cursor = InventoryManager.ConstructionCursor;
        if (cursor == null || !cursor.gameObject.activeInHierarchy)
            return;
        if (!MirrorUtil.CanBuildMirrored(cursor, out string reason))
        {
            MirrorUtil.Refuse(cursor, reason);
            return;
        }
        MirrorState.CursorVariant = MirrorState.CursorVariant == MirrorVariant.Off ? AxisForViewMirror(cursor) : MirrorVariant.Off;
        MirrorState.CursorKit = MirrorState.CursorVariant == MirrorVariant.Off ? null : CurrentKit();
        Synchronize();
    }

    // Owner, 2026-10-16: keep M through an interrupted build, but switch it off when the kit is
    // put away or another kit is taken. Cycling variants of the same kit keeps the same item.
    internal static void CheckKit()
    {
        if (!Plugin.OneBuildPerPress || MirrorState.CursorVariant == MirrorVariant.Off)
            return;
        var kit = CurrentKit();
        if (kit && ReferenceEquals(kit, MirrorState.CursorKit))
            return;
        MirrorState.CursorVariant = MirrorVariant.Off;
        MirrorState.CursorKit = null;
        Synchronize();
    }

    private static Thing? CurrentKit()
    {
        var inventory = InventoryManager.Instance;
        if (!inventory || inventory.ActiveHand == null || inventory.ActiveHand.Slot == null)
            return null;
        var occupant = inventory.ActiveHand.Slot.Get();
        return occupant ? occupant : null;
    }

    // Reflect directly along the cursor axis nearest camera-right. No root turn.
    private static byte AxisForViewMirror(Structure cursor)
    {
        var camera = Assets.Scripts.CameraController.CurrentCamera;
        if (camera == null)
            return MirrorVariant.X;
        var t = cursor.ThingTransform;
        var cameraRight = camera.transform.right;
        // Snap the camera's right to the cursor's nearest local axis so the result stays on the grid.
        return MirrorVariant.NearestAxis(Vector3.Dot(t.right, cameraRight),
            Vector3.Dot(t.up, cameraRight), Vector3.Dot(t.forward, cameraRight));
    }
}

[HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.UpdatePlacement), new[] { typeof(Constructor) })]
internal static class ConstructorCursorPatch
{
    private static void Postfix() => CursorMirror.Synchronize();
}

[HarmonyPatch(typeof(InventoryManager), nameof(InventoryManager.UpdatePlacement), new[] { typeof(Structure) })]
internal static class StructureCursorPatch
{
    private static void Postfix() => CursorMirror.Synchronize();
}
