namespace StationeersMirror;

// This is deliberately a name heuristic, not knowledge of Animator bindings.
internal static class AnimatorControlPolicy
{
    private static readonly string[] Controls = { "switch", "button", "lever", "knob", "dial", "toggle", "rocker", "onoff" };
    private static readonly string[] Mechanisms = { "door", "panel", "flap", "hatch", "lid", "cover", "drawer", "slide" };

    internal static bool IsControlName(string name) => Contains(name, Controls) && !Contains(name, Mechanisms);
    private static bool Contains(string name, string[] words)
    {
        foreach (string word in words)
            if (name.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

}

