namespace StationeersMirror;

internal enum PartKind { Control, Mechanism, Ambiguous }

// Kept independent of Unity so the action policy can be exercised offline.
internal static class ControlPolicy
{
    internal static PartKind Classify(string action, bool knownMechanism = false)
    {
        if (knownMechanism || action == "Open" || action == "Lock")
            return PartKind.Mechanism;
        if (action == "OnOff" || action == "Mode" || action == "Activate" ||
            action.StartsWith("Button", System.StringComparison.Ordinal) &&
            int.TryParse(action.Substring(6), out int button) && button >= 1 && button <= 17)
            return PartKind.Control;
        return PartKind.Ambiguous;
    }
}
