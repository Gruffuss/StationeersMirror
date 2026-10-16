using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using DefaultNamespace;
using Objects;
using UnityEngine;
using static StationeersMirror.MirrorDrivenControls;

namespace StationeersMirror;

// Explicit held references only; action/driver identity classifies the part.
// Motion axes and keyframe poses no longer affect facing or eligibility.
internal static class MirrorControlDiscovery
{
    internal static void Visit(MonoBehaviour component, Action<Transform?, string, string?> add)
    {
        if (component is IInternalCombustion combustion)
        {
            add(combustion.ThrottleLever, "InternalCombustion.ThrottleLever", null);
            add(combustion.CombustionLever, "InternalCombustion.CombustionLever", null);
        }
        switch (component)
        {
            case SwitchOnOff rocker:
                var action = rocker is SwitchMode mode ? mode.State : InteractableType.OnOff;
                Add(Read<Transform>(rocker, "switchTransform"), "switchTransform action=" + action,
                    ControlPolicy.Classify(action.ToString()), add);
                break;
            case ActivateButton button:
                add(Read<Transform>(button, "buttonTransform"), "buttonTransform action=Activate (position)", null);
                break;
            case InteractableAnimComponent animation:
                VisitAnimation(animation, add);
                break;
            case BlendedAnimComponent blended:
                Add(Read<Transform>(blended, "animatedTransform"), "animatedTransform",
                    blended is DialKnob ? PartKind.Control : PartKind.Ambiguous, add);
                break;
            case IndustrialBurner burner:
                add(Read<Transform>(burner, "temperatureGaugeTransform"), "temperatureGaugeTransform (position indicator)", null);
                break;
            case StateChangeDevice device:
                add(device.temperatureGaugeTransform, "temperatureGaugeTransform (position indicator)", null);
                break;
            case RotateAround rotation:
                add(rotation.Transform, "Transform", "ambiguous perpetual rotator without a control action; stays mirrored");
                break;
            case Assets.Scripts.Objects.Electrical.Helper.Rotator rotation:
                add(rotation.Transform, "Transform", "ambiguous perpetual rotator without a control action; stays mirrored");
                break;
        }
    }
    private static void VisitAnimation(InteractableAnimComponent animation, Action<Transform?, string, string?> add)
    {
        string source = "ObjectAnimData action=" + animation.AssignedAction;
        var kind = ControlPolicy.Classify(animation.AssignedAction.ToString(),
            animation is MultiStateDoorAnimComponent || animation is SlidingPanelAnimationComponent ||
            animation is OpenAnimationComponent || animation is ContainerOpenAnimComponent);
        IEnumerable<AnimKeyFrameCollection> collections;
        if (animation is BinaryTransformAnimComponent)
            collections = new[] { Read<AnimKeyFrameCollection>(animation, "state0"), Read<AnimKeyFrameCollection>(animation, "state1") };
        else if (animation is MultiStateAnimComponent)
            collections = Read<AnimKeyFrameCollection[]>(animation, "states") ?? Array.Empty<AnimKeyFrameCollection>();
        else
        {
            if (Plugin.DebugEnabled && !(animation is BinaryMaterialAnimComponent))
                Plugin.Log?.LogInfo($"Mirror control ambiguous: {animation.GetType().Name} {source}: unknown keyframe family.");
            return;
        }
        var nodes = new HashSet<Transform>();
        foreach (var collection in collections)
        {
            if (collection == null || collection.AnimData == null) continue;
            foreach (var data in collection.AnimData)
                if (data != null && data.transform) nodes.Add(data.transform);
        }
        foreach (var node in nodes) Add(node, source, kind, add);
    }
    private static void Add(Transform? node, string source, PartKind kind, Action<Transform?, string, string?> add) =>
        add(node, source, kind == PartKind.Mechanism ? "mechanism stays a mirror image" :
            kind == PartKind.Ambiguous ? "ambiguous action/driver stays a mirror image" : null);
}
