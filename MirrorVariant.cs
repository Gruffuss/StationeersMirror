namespace StationeersMirror;

// Persisted mirror axes: do not renumber. Legacy save marker 1 means root X.
internal static class MirrorVariant
{
    internal const byte Off = 0, X = 1, Y = 2, Z = 3;

    internal static byte Normalize(int value) => value >= X && value <= Z ? (byte)value : Off;

    // Inputs are dot products of cursor right/up/forward with camera-right.
    // Sign does not change a reflection plane; ties prefer X, then Y.
    internal static byte NearestAxis(float x, float y, float z) =>
        System.Math.Abs(x) >= System.Math.Abs(y) && System.Math.Abs(x) >= System.Math.Abs(z) ? X :
        System.Math.Abs(y) >= System.Math.Abs(z) ? Y : Z;

    internal static string AxisName(byte variant) => variant switch
    {
        X => "X",
        Y => "Y",
        Z => "Z",
        _ => "off"
    };
}
