using System.Reflection;
using Assets.Scripts.Objects;
using Objects;
using UnityEngine;
using static StationeersMirror.MirrorDrivenControls;

namespace StationeersMirror;

// Runtime Debug config only. Watch the whole hierarchy without changing poses,
// sampling clips, or rebinding Animator paths (prefab bindings are unknown).
internal static class MirrorProbe
{
    private readonly struct Pose
    {
        internal readonly Vector3 Position;
        internal readonly Quaternion Rotation;
        internal Pose(Transform node) { Position = node.localPosition; Rotation = node.localRotation; }
    }

    private sealed class Watch
    {
        internal readonly WeakReference<Structure> Owner;
        internal readonly Dictionary<Transform, Pose> Last = new();
        internal Dictionary<Transform, string> Drivers = new();
        internal int Frames;
        internal Watch(Structure owner) { Owner = new WeakReference<Structure>(owner); }
    }

    private static readonly List<Watch> Watches = new();

    internal static void Register(Structure structure)
    {
        if (!Plugin.DebugEnabled || structure.IsCursor) return;
        Watches.RemoveAll(w => !w.Owner.TryGetTarget(out var owner) || !owner || owner == structure);
        var root = structure.ThingTransform;
        string label = Label(structure);
        var animators = structure.GetComponentsInChildren<Animator>(true).Where(a => a)
            .Select(a => Path(root, a.transform));
        Plugin.Log?.LogInfo($"Mirror probe: {label} animators=[{string.Join(", ", animators)}]");
        foreach (var component in structure.GetComponentsInChildren<Component>(true))
        {
            if (!component) continue;
            if (component is DevicePart || component is InteractableAnimComponent ||
                component is BlendedAnimComponent || component is Animator)
                Plugin.Log?.LogInfo($"Mirror probe: {label} component={component.GetType().FullName} " +
                    $"path={Path(root, component.transform)}" +
                    (component is InteractableAnimComponent anim ? $" AssignedAction={anim.AssignedAction}" : ""));
        }
        // Thing's field identifies the switch path independently of an Animator.
        var rocker = structure.SwitchOnOff;
        Plugin.Log?.LogInfo($"Mirror probe: {label} Thing.SwitchOnOff=" +
            (rocker ? rocker.GetType().Name + "@" + Path(root, rocker.transform) : "(none)"));
        foreach (var interactable in structure.Interactables)
        {
            var collider = interactable.Collider;
            if (collider)
                Plugin.Log?.LogInfo($"Mirror probe: {label} {interactable.Action} collider={Path(root, collider.transform)}");
        }
        var watch = new Watch(structure) { Drivers = MapDrivers(structure, true) };
        if (MirrorState.Instances.TryGetValue(structure, out var facingRecord))
            MirrorFacingProbe.Log(structure, facingRecord.DrivenControls.Select(c => c.Node)
                .Concat(facingRecord.AnimatorControls.Select(c => c.Node)));
        foreach (var node in structure.GetComponentsInChildren<Transform>(true))
            if (node && !(node is RectTransform)) watch.Last[node] = new Pose(node);
        Watches.Add(watch);
    }

    internal static void Tick()
    {
        if (!Plugin.DebugEnabled) return;
        for (int i = Watches.Count - 1; i >= 0; i--)
        {
            var watch = Watches[i];
            if (!watch.Owner.TryGetTarget(out var owner) || !owner || owner.IsCursor || !MirrorState.IsMirrored(owner))
            {
                Watches.RemoveAt(i);
                continue;
            }
            // Physics bounds lag the transform until the next physics sync; check after a few frames.
            if (++watch.Frames == 5) CheckBoxColliders(owner);
            var nodes = owner.GetComponentsInChildren<Transform>(true).Where(n => n && !(n is RectTransform)).ToArray();
            var live = new HashSet<Transform>(nodes);
            bool changedHierarchy = nodes.Any(n => !watch.Last.ContainsKey(n)) || watch.Last.Keys.Any(n => !live.Contains(n));
            if (changedHierarchy) watch.Drivers = MapDrivers(owner, false);
            foreach (var removed in watch.Last.Keys.Where(n => !live.Contains(n)).ToArray()) watch.Last.Remove(removed);
            foreach (var node in nodes)
            {
                var now = new Pose(node);
                if (watch.Last.TryGetValue(node, out var before) &&
                    (!now.Position.Equals(before.Position) || !now.Rotation.Equals(before.Rotation)))
                {
                    string driver = watch.Drivers.TryGetValue(node, out var mapped) ? mapped : "(unmapped)";
                    string animators = string.Join(", ", node.GetComponentsInParent<Animator>(true)
                        .Where(a => a).Select(a => Path(owner.ThingTransform, a.transform)));
                    Plugin.Log?.LogInfo($"Mirror probe: {Label(owner)} moved {Path(owner.ThingTransform, node)} " +
                        $"localPosition {before.Position.ToString("G9")} -> {now.Position.ToString("G9")}; " +
                        $"localRotation {before.Rotation.ToString("G9")} -> {now.Rotation.ToString("G9")}; " +
                        $"driver={driver}; Animator ancestors=[{animators}] (clip bindings unknown)");
                }
                watch.Last[node] = now;
            }
        }
    }

    private static Dictionary<Transform, string> MapDrivers(Structure owner, bool log)
    {
        var result = new Dictionary<Transform, string>();
        foreach (var part in Discover(owner))
        {
            bool proxied = MirrorState.Instances.TryGetValue(owner, out var record) &&
                record.DrivenControls.Any(c => c.Node == part.Node && c.Proxy);
            string description = part.Source + "; " + (part.Reason ?? "script control") + $"; proxy={proxied}";
            result[part.Node] = description;
            if (log)
                Plugin.Log?.LogInfo($"Mirror probe: {Label(owner)} target={Path(owner.ThingTransform, part.Node)} driver={description}");
        }
        if (MirrorState.Instances.TryGetValue(owner, out var mirror))
            foreach (var control in mirror.AnimatorControls)
                if (control.Node)
                    result[control.Node] = "Animator control name heuristic; pose logged after correction; clip bindings unknown";
        // These references also name mechanisms and unclassified fields for the
        // next audit, but are explicitly not presented as proof of a pose writer.
        foreach (var component in owner.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (!component) continue;
            for (Type? type = component.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType != typeof(Transform)) continue;
                var target = (Transform?)field.GetValue(component);
                if (!target || result.ContainsKey(target!)) continue;
                result[target!] = $"reference only: {component.GetType().Name}.{field.Name}@{Path(owner.ThingTransform, component.transform)} (writer unconfirmed)";
            }
        }
        return result;
    }

    // Unity logs "BoxCollider does not support negative scale or size" for every box under a
    // mirrored root (35 in the owner's log, 2026-10-16) and forces the size positive. Whether
    // the physics box still sits where the mirrored geometry is, is not documented: measure it.
    // Expected: the box centre through the full (mirrored) matrix, and |scale * size|. The
    // physics AABB (collider.bounds) is compared for a collider aligned with world axes only,
    // where the AABB equals the box; others report the centre alone.
    private static void CheckBoxColliders(Structure owner)
    {
        int checkedCount = 0, wrong = 0;
        foreach (var box in owner.GetComponentsInChildren<BoxCollider>(true))
        {
            if (!box || !box.enabled || !box.gameObject.activeInHierarchy) continue;
            checkedCount++;
            var t = box.transform;
            var expectedCentre = t.TransformPoint(box.center);
            var actual = box.bounds;
            float centreError = (actual.center - expectedCentre).magnitude;
            string sizeNote = "";
            bool axisAligned = Mathf.Abs(Mathf.Abs(Vector3.Dot(t.right, Vector3.right)) - 1f) < 1e-4f &&
                Mathf.Abs(Mathf.Abs(Vector3.Dot(t.up, Vector3.up)) - 1f) < 1e-4f;
            float sizeError = 0f;
            if (axisAligned)
            {
                var expectedSize = Vector3.Scale(t.lossyScale, box.size);
                expectedSize = new Vector3(Mathf.Abs(expectedSize.x), Mathf.Abs(expectedSize.y), Mathf.Abs(expectedSize.z));
                sizeError = (actual.size - expectedSize).magnitude;
                sizeNote = $" size {actual.size.ToString("G4")} expected {expectedSize.ToString("G4")}";
            }
            if (centreError > 1e-3f || sizeError > 1e-3f)
            {
                wrong++;
                Plugin.Log?.LogWarning($"Mirror probe: {Label(owner)} box collider {Path(owner.ThingTransform, t)} " +
                    $"centre {actual.center.ToString("G6")} expected {expectedCentre.ToString("G6")} (off {centreError:G3}){sizeNote}");
            }
        }
        Plugin.Log?.LogInfo($"Mirror probe: {Label(owner)} box colliders checked={checkedCount} mismatched={wrong} (centre/size vs mirrored geometry).");
    }

    private static string Label(Structure owner) => $"{owner.PrefabName} #{owner.ReferenceId} axis={MirrorVariant.AxisName(MirrorState.GetVariant(owner))}";
}
