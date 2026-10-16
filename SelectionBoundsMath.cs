using System;

namespace StationeersMirror;

internal static class SelectionBoundsMath
{
    internal const float Padding = 0.01f;
    internal static int AlignedAxis(float x, float y, float z) =>
        Math.Abs(x) >= 0.999f ? 0 : Math.Abs(y) >= 0.999f ? 1 : Math.Abs(z) >= 0.999f ? 2 : -1;

    internal static float RestoreVanillaExtent(float finalExtent, float currentScale)
    {
        // SetMinMax and the two sequential setters leave e = raw + Padding,
        // even if raw or the final extent is negative (or crosses through zero).
        if (currentScale == 0f || float.IsNaN(currentScale) || float.IsInfinity(currentScale)) return finalExtent;
        float rawMagnitude = Math.Abs(finalExtent - Padding);
        float vanillaRaw = currentScale < 0f ? rawMagnitude : -rawMagnitude;
        return Math.Abs(vanillaRaw + Padding);
    }
}
