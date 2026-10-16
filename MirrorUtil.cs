using System.Reflection;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Structures;
using HarmonyLib;
using Networks;
using Objects.Rockets;
using TMPro;
using UnityEngine;
using GameConsole = Assets.Scripts.Objects.Electrical.Console;

namespace StationeersMirror;

internal sealed class MirrorRecord
{
    internal byte Variant;
    internal GridBounds GridBounds = null!;
    internal Bounds Bounds;
    internal Vector3 RootScale;
    internal Vector3[]? Blocking;
    internal StructureRenderMode RenderMode;
    internal readonly Dictionary<Transform, TransformPose> Poses = new();
    internal readonly Dictionary<Transform, Vector3> TextScales = new();
    internal readonly Dictionary<DynamicThing, Vector3> OccupantScales = new();
    internal readonly List<GameObject> CreatedRenderers = new();
    internal Action? RestoreRendering;
    internal List<DrivenControl> DrivenControls = new();
    internal List<AnimatorControl> AnimatorControls = new();
}

internal readonly struct TransformPose
{
    private readonly Vector3 _position;
    private readonly Quaternion _rotation;
    private readonly Vector3 _scale;

    internal TransformPose(Transform transform)
    {
        _position = transform.localPosition;
        _rotation = transform.localRotation;
        _scale = transform.localScale;
    }

    internal void Restore(Transform transform)
    {
        transform.localPosition = _position;
        transform.localRotation = _rotation;
        transform.localScale = _scale;
    }
}

internal static class MirrorUtil
{
    private static readonly AccessTools.FieldRef<Structure, Vector3[]> Blocking =
        AccessTools.FieldRefAccess<Structure, Vector3[]>("blockingGrids");
    private static readonly AccessTools.FieldRef<Connection, bool> ConnectionInitialized =
        AccessTools.FieldRefAccess<Connection, bool>("_isInitialized");
    private static readonly Dictionary<Type, FieldInfo[]> ConnectionFields = new();
    private static readonly ConditionalWeakTable<Structure, ExpectedConnections> Diagnostics = new();
    private static readonly ConditionalWeakTable<Structure, object> ReportedFailures = new();

    private sealed class ExpectedConnections
    {
        internal readonly List<(Connection Connection, Vector3 Position, Vector3 Forward)> Values = new();
    }

    // Building only (owner, 2026-10-16: "you should not be able to mirror cables and pipes").
    // Loading and network still go through CanMirror alone, so pipes and cables already saved
    // mirrored keep their geometry and connections.
    internal static bool CanBuildMirrored(Structure structure, out string reason)
    {
        if (structure is Assets.Scripts.Objects.Pipes.Pipe || structure is Cable)
        {
            reason = "pipes and cables are not mirrored";
            return false;
        }
        return CanMirror(structure, out reason);
    }

    internal static bool CanMirror(Structure structure, out string reason)
    {
        reason = structure switch
        {
            GameConsole or LogicDisplay => "screen orientation uses transform.right",
            Wall or WindowShutterController or Seat or LaunchMount => "orientation uses transform.right",
            INetworkedRocketPart => "rocket hull mirroring is not supported",
            _ => string.Empty
        };
        return reason.Length == 0;
    }

    internal static void Refuse(Structure structure, string reason)
    {
        string message = $"StationeersMirror: cannot mirror {structure.GetType().Name}: {reason}.";
        ConsoleWindow.Print(message);
        Plugin.Log?.LogInfo(message);
    }

    internal static Vector3 Reflect(Vector3 value, byte axis) =>
        MirrorControlGeometry.Vector(ControlTransformMath.Reflect(MirrorControlGeometry.Vector(value), axis));
    internal static Vector3 Axis(byte axis) => axis == MirrorVariant.X ? Vector3.right :
        axis == MirrorVariant.Y ? Vector3.up : axis == MirrorVariant.Z ? Vector3.forward : Vector3.zero;
    private static Vector3 ReflectDirection(Transform root, Vector3 world, byte axis) =>
        root.TransformDirection(Reflect(root.InverseTransformDirection(world), axis));

    internal static int CounterFlipAxis(Structure owner, Transform node, byte axis)
    {
        var local = node.InverseTransformVector(owner.ThingTransform.TransformVector(Axis(axis))).normalized;
        int aligned = SelectionBoundsMath.AlignedAxis(local.x, local.y, local.z);
        if (aligned < 0 && Plugin.DebugEnabled)
            Plugin.Log?.LogInfo($"Mirror counter-flip skipped: {owner.PrefabName}/{MirrorDrivenControls.Path(owner.ThingTransform, node)} is off axis {MirrorVariant.AxisName(axis)}.");
        return aligned;
    }

    // Includes separately serialized fields on vanilla and modded devices. A shared
    // Connection is visited once; distinct Connection objects may still share a Transform.
    internal static List<Connection> GetConnections(Structure structure)
    {
        var result = new HashSet<Connection>();
        if (structure is SmallGrid smallGrid)
            foreach (var connection in smallGrid.OpenEnds)
                if (connection != null)
                    result.Add(connection);

        Type type = structure.GetType();
        if (!ConnectionFields.TryGetValue(type, out var fields))
        {
            var found = new List<FieldInfo>();
            for (Type? current = type; current != null && typeof(Structure).IsAssignableFrom(current); current = current.BaseType)
                found.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => typeof(Connection).IsAssignableFrom(field.FieldType) ||
                                    typeof(IEnumerable<Connection>).IsAssignableFrom(field.FieldType)));
            fields = found.ToArray();
            ConnectionFields.Add(type, fields);
        }
        foreach (var field in fields)
        {
            object? value = field.GetValue(structure);
            if (value is Connection connection)
                result.Add(connection);
            else if (value is IEnumerable<Connection> connections)
                foreach (var item in connections)
                    if (item != null)
                        result.Add(item);
        }
        return result.ToList();
    }

    internal static bool Apply(Structure structure, byte variant)
    {
        variant = MirrorVariant.Normalize(variant);
        if (MirrorState.GetVariant(structure) == variant)
            return true;
        if (!CanMirror(structure, out string reason))
        {
            Refuse(structure, reason);
            return false;
        }

        Transform root = structure.ThingTransform;
        var connections = GetConnections(structure);
        byte diagnosticAxis = MirrorState.GetVariant(structure);
        if (diagnosticAxis == MirrorVariant.Off) diagnosticAxis = variant;
        var expected = new ExpectedConnections();
        foreach (var connection in connections)
            if (connection.Transform != null)
                expected.Values.Add((connection,
                    Reflect(root.InverseTransformDirection(connection.Transform.position - root.position), diagnosticAxis),
                    Reflect(root.InverseTransformDirection(connection.Transform.forward), diagnosticAxis)));
        Diagnostics.Remove(structure);
        if (Plugin.DebugEnabled)
            Diagnostics.Add(structure, expected);

        if (MirrorState.Instances.TryGetValue(structure, out var original))
        {
            try
            {
                RestoreGeometry(structure, original);
                MirrorState.Instances.Remove(structure);
                MirrorRendering.Restore(structure, original.RenderMode);
            }
            catch (Exception error) { throw Rollback(structure, original, error); }
            // A cached cursor or a join update can replace one mirrored variant
            // with another. Re-capture only after restoring the vanilla geometry.
            if (variant != MirrorVariant.Off)
                return Apply(structure, variant);
        }
        else
        {
            original = new MirrorRecord
            {
                Variant = variant,
                GridBounds = structure.GridBounds,
                Bounds = structure.Bounds,
                RootScale = root.localScale,
                Blocking = Blocking(structure),
                RenderMode = structure.structureRenderMode
            };
            // Publish the recovery record before the first mutation. Batch guards
            // must also see the mirror while renderer preparation runs.
            original.RestoreRendering = MirrorRendering.CaptureRollback(structure, original);
            MirrorState.Instances.Add(structure, original);
            try
            {
                MirrorRendering.PrepareUnityRenderers(structure, original);
                if (Plugin.VanillaControls)
                {
                    original.DrivenControls = MirrorDrivenControls.Capture(structure, connections);
                    original.AnimatorControls = MirrorAnimatorControls.Capture(structure, original, connections);
                }
                // Capture all directions before changing any parent transform.
                var directions = connections.Where(c => c.Transform != null)
                    .Select(c => c.Transform).Where(t => t != root).Distinct()
                    .Select(t => (Transform: t, Position: root.TransformPoint(Reflect(root.InverseTransformPoint(t.position), variant)),
                        Forward: ReflectDirection(root, t.forward, variant), Up: ReflectDirection(root, t.up, variant)))
                    .OrderBy(item => Depth(item.Transform)).ToArray();
                var textRoots = FindTextRoots(root, original.DrivenControls, original.AnimatorControls);
                foreach (var item in directions)
                    Capture(original, item.Transform);
                var triggers = MirrorControlTriggers.Plan(structure,
                    original.DrivenControls.Select(c => new MirrorControlTriggers.Group(c.Node, c.AroundRest))
                        .Concat(original.AnimatorControls.Select(c => new MirrorControlTriggers.Group(c.Node, c.AroundRest))),
                    new HashSet<Transform>(directions.Select(d => d.Transform).Concat(textRoots)));
                foreach (var trigger in triggers)
                    Capture(original, trigger.Node);
                var textFlips = textRoots.Select(t => (Node: t, Axis: CounterFlipAxis(structure, t, variant)))
                    .Where(t => t.Axis >= 0).ToArray();
                foreach (var text in textFlips)
                    original.TextScales.Add(text.Node, text.Node.localScale);

                // The root owns the reflection, including root renderers/colliders and
                // children whose local poses are subsequently overwritten by animation.
                root.localScale = Reflect(root.localScale, variant);
                MirrorDrivenControls.Install(original.DrivenControls);
                MirrorAnimatorControls.Install(structure, original.AnimatorControls);
                foreach (var trigger in triggers)
                    MirrorControlGeometry.Write(trigger.Node, trigger.Pose);
                foreach (var text in textFlips)
                    text.Node.localScale = Reflect(text.Node.localScale, (byte)(text.Axis + 1));
                foreach (var item in directions)
                    item.Transform.SetPositionAndRotation(item.Position, Quaternion.LookRotation(item.Forward, item.Up));

                structure.GridBounds = MirrorBounds(original.GridBounds, variant);
                structure.Bounds = new Bounds(Reflect(original.Bounds.center, variant), original.Bounds.size);
                if (original.Blocking != null)
                    Blocking(structure) = original.Blocking.Select(p => Reflect(p, variant)).ToArray();
                MirrorOccupants.ApplyExisting(structure, original);
                MirrorRendering.UseUnityRenderers(structure);
                InvalidateConnections(structure, connections);
                MirrorProbe.Register(structure);
                if (Plugin.DebugEnabled)
                    Plugin.Log?.LogInfo($"Mirror {structure.PrefabName} #{structure.ReferenceId}: axis={MirrorVariant.AxisName(variant)} (variant={variant}).");
            }
            catch (Exception error) { throw Rollback(structure, original, error); }
        }
        return true;
    }

    private static void InvalidateConnections(Structure structure, IEnumerable<Connection> connections)
    {
        // OnAssignedReference snaps the root before Register. Do not freeze grids
        // from the pre-snap position: GetLocalGrid/GetFacingGrid use live transforms
        // while this flag is false. Registration/rebuild then fills the caches.
        foreach (var connection in connections)
        {
            ConnectionInitialized(connection) = false;
            connection.Validate();
            connection.CacheTransformUp();
        }
        if (structure.IsCursor)
            RefreshConnections(connections);
    }

    private static void RestoreGeometry(Structure structure, MirrorRecord original)
    {
        var recovery = new MirrorRecovery();
        recovery.Try(() => structure.ThingTransform.localScale = original.RootScale);
        recovery.Try(() => MirrorAnimatorControls.Restore(structure, original.AnimatorControls));
        recovery.Try(() => MirrorDrivenControls.Restore(original.DrivenControls));
        foreach (var pair in original.Poses)
            if (pair.Key != null)
                recovery.Try(() => pair.Value.Restore(pair.Key));
        foreach (var pair in original.TextScales)
            if (pair.Key != null)
                recovery.Try(() => pair.Key.localScale = pair.Value);
        recovery.Try(() => MirrorOccupants.Restore(structure, original));
        recovery.Try(() => structure.GridBounds = original.GridBounds);
        recovery.Try(() => structure.Bounds = original.Bounds);
        recovery.Try(() => Blocking(structure) = original.Blocking!);
        recovery.Try(() => InvalidateConnections(structure, GetConnections(structure)));
        recovery.ThrowIfFailed();
    }

    private static Exception Rollback(Structure structure, MirrorRecord original, Exception error)
    {
        var recovery = new MirrorRecovery();
        recovery.Try(() => RestoreGeometry(structure, original));
        MirrorState.Instances.Remove(structure);
        Diagnostics.Remove(structure);
        recovery.Try(() => original.RestoreRendering?.Invoke());
        return recovery.Combine(error);
    }

    internal static void ReportFailure(Structure structure, Exception error)
    {
        if (ReportedFailures.TryGetValue(structure, out _))
            return;
        ReportedFailures.Add(structure, new object());
        Plugin.Log?.LogError($"Mirror disabled for {structure.GetType().Name} #{structure.ReferenceId}: {error}");
    }

    private static void Capture(MirrorRecord record, Transform transform)
    {
        if (!record.Poses.ContainsKey(transform))
            record.Poses.Add(transform, new TransformPose(transform));
    }

    private static int Depth(Transform transform)
    {
        int depth = 0;
        for (Transform parent = transform.parent; parent != null; parent = parent.parent)
            depth++;
        return depth;
    }

    private static Transform[] FindTextRoots(Transform root, IEnumerable<DrivenControl> controls, IEnumerable<AnimatorControl> animatorControls)
    {
        var candidates = new HashSet<Transform>(root.GetComponentsInChildren<Canvas>(true).Select(c => c.transform));
        candidates.UnionWith(root.GetComponentsInChildren<TMP_Text>(true).Select(c => c.transform));
        candidates.UnionWith(root.GetComponentsInChildren<TextMesh>(true).Select(c => c.transform));
        // Counter-flip text subtrees only, never cancel the structure's root flip.
        candidates.Remove(root);
        // Occupants receive one whole-object compensation, including their text.
        candidates.RemoveWhere(t => MirrorOccupants.IsInsideOccupant(t, root));
        candidates.RemoveWhere(t => MirrorDrivenControls.Contains(controls, t));
        candidates.RemoveWhere(t => MirrorAnimatorControls.Contains(animatorControls, t));
        return candidates.Where(t => !HasTextAncestor(t, root, candidates)).ToArray();
    }

    private static bool HasTextAncestor(Transform transform, Transform root, HashSet<Transform> candidates)
    {
        for (Transform parent = transform.parent; parent != null && parent != root; parent = parent.parent)
            if (candidates.Contains(parent))
                return true;
        return false;
    }

    internal static GridBounds MirrorBounds(GridBounds bounds, byte axis) => new()
    {
        _grids = bounds._grids.Select(grid => ReflectGrid(grid, axis)).ToArray(),
        _gridsSmall = bounds._gridsSmall.Select(grid => ReflectGrid(grid, axis)).ToArray(),
        BoundsBig = new Bounds(Reflect(bounds.BoundsBig.center, axis), bounds.BoundsBig.size),
        BoundsSmall = new Bounds(Reflect(bounds.BoundsSmall.center, axis), bounds.BoundsSmall.size)
    };

    private static Grid3 ReflectGrid(Grid3 grid, byte axis) => new(
        axis == MirrorVariant.X ? -grid.x : grid.x,
        axis == MirrorVariant.Y ? -grid.y : grid.y,
        axis == MirrorVariant.Z ? -grid.z : grid.z);

    internal static void RefreshConnections(IEnumerable<Connection> connections)
    {
        foreach (var connection in connections)
        {
            connection.Validate();
            connection.SetGrids();
            connection.CacheTransformUp();
        }
    }

    internal static void SetRegisteredMirror(Structure structure, byte variant)
    {
        variant = MirrorVariant.Normalize(variant);
        if (MirrorState.GetVariant(structure) == variant)
            return;
        if (!CanMirror(structure, out string reason))
        {
            Refuse(structure, reason);
            return;
        }
        try
        {
            structure.DetatchFromGrid();
            Apply(structure, variant);
            structure.RebuildGridState();
            RefreshConnections(GetConnections(structure));
            structure.AttachToGrid();
            CheckRegistered(structure);
        }
        catch (Exception error)
        {
            var recovery = new MirrorRecovery();
            // Attach can itself fail partway through. Remove any partial mirrored
            // registration before restoring geometry and rebuilding vanilla cells.
            recovery.Try(() => structure.DetatchFromGrid());
            if (MirrorState.Instances.TryGetValue(structure, out var record))
                error = Rollback(structure, record, error);
            recovery.Try(() => structure.RebuildGridState());
            recovery.Try(() => RefreshConnections(GetConnections(structure)));
            recovery.Try(() => structure.AttachToGrid());
            throw recovery.Combine(error);
        }
    }

    internal static void CheckRegistered(Structure structure)
    {
        if (!Plugin.DebugEnabled || structure.IsCursor || !Diagnostics.TryGetValue(structure, out var expected))
            return;
        Diagnostics.Remove(structure);
        foreach (var item in expected.Values)
        {
            Vector3 position = structure.ThingTransform.position + structure.ThingTransform.TransformDirection(item.Position);
            Vector3 forward = structure.ThingTransform.TransformDirection(item.Forward);
            Grid3 local = structure.GridController.WorldToLocalGrid(position, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
            Grid3 facing = structure.GridController.WorldToLocalGrid(position + forward * SmallGrid.SmallGridSize, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
            string message = $"{structure.PrefabName} #{structure.ReferenceId} {item.Connection.Transform.name}: LocalGrid={item.Connection.LocalGrid} expected={local}; FacingGrid={item.Connection.FacingGrid} expected={facing}";
            Plugin.Log?.LogInfo(message);
            if (item.Connection.LocalGrid != local || item.Connection.FacingGrid != facing)
                Plugin.Log?.LogWarning($"Mirror grid mismatch: {message}");
        }
        var occupied = new Grid3[structure.GridBounds._grids.Length];
        structure.GridBounds.GetLocalGrids(structure.ThingTransformPosition, structure.ThingTransformRotation, occupied);
        var small = (Grid3[])structure.GridBounds.GetLocalSmallGrid(structure.ThingTransformPosition, structure.ThingTransformRotation);
        Plugin.Log?.LogInfo($"{structure.PrefabName} #{structure.ReferenceId} occupied=[{string.Join(", ", occupied)}]; small=[{string.Join(", ", small)}]; blocking=[{string.Join(", ", structure.BlockingGrids)}]");
    }
}
