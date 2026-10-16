using System.Numerics;
using StationeersMirror;

internal static class VariantChecks
{
    internal static void Run()
    {
        Require(MirrorVariant.Normalize(1) == MirrorVariant.X, "legacy State 1 is X");
        foreach (int value in new[] { int.MinValue, -1, 0, 4, 255, 256, 257, int.MaxValue })
            Require(MirrorVariant.Normalize(value) == 0, "invalid save value off before byte conversion");
        foreach (byte axis in new byte[] { 1, 2, 3 })
        {
            Require(MirrorVariant.Normalize((int)axis) == axis, "save State round trip");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(axis); writer.Write((byte)0x7b);
            Require(stream.Length == 2, "variant occupies exactly one byte");
            stream.Position = 0;
            using var reader = new BinaryReader(stream);
            Require(reader.ReadByte() == axis && reader.ReadByte() == 0x7b, "variant byte round trip preserves next field");
            foreach (float sign in new[] { -1f, 1f })
            {
                var right = Vector3.Zero; right[axis - 1] = sign;
                Require(MirrorVariant.NearestAxis(right.X, right.Y, right.Z) == axis, "camera-right nearest signed local axis");
            }
        }
        Require(MirrorVariant.NearestAxis(1, 1, 1) == 1 && MirrorVariant.NearestAxis(0, -1, 1) == 2, "stable axis ties");
        var p0 = new Vector3(0, .225f, .0535f);
        var a = ControlTransformMath.AroundRest(p0, MirrorVariant.Z);
        var mirror = ControlTransformMath.Mirror(MirrorVariant.Z);
        Require(ControlTransformMath.TrySolve(Matrix4x4.Identity, mirror, a, out var g), "regulator solve");
        foreach (float sign in new[] { -1f, 1f })
        {
            var trigger = p0 + new Vector3(sign * .06f, 0, 0);
            var actual = Vector3.Transform(Vector3.Zero, Matrix4x4.CreateTranslation(trigger) * g * mirror);
            Near(actual, new Vector3(sign * .06f, .225f, -.0535f), "regulator trigger moves across Z plane");
            Require(actual.X == trigger.X, "Z reflection cannot exchange supplied +/-X trigger offsets");
        }
        foreach (float angle in new[] { -.2f, .2f })
        {
            var wheel = Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(p0);
            Near(Vector3.TransformNormal(Vector3.UnitX, wheel * g * mirror), Vector3.TransformNormal(Vector3.UnitX, wheel), "regulator wheel keeps vanilla turn direction");
        }
        foreach (var pivot in new[] { p0, new Vector3(.484f, .445f, .306f) })
        {
            var oldIdentity = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateTranslation(-pivot.X, pivot.Y, pivot.Z);
            Require(ControlTransformMath.Near(ControlTransformMath.AroundRest(pivot, MirrorVariant.X), oldIdentity), "Computer/Larre variant 1 unchanged");
        }
        Console.WriteLine("PASS axis variants: legacy 1=X, save values and byte round trips, camera axis/ties; regulator Z trigger positions and vanilla wheel motion; Computer/Larre X identity.");
    }
    private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < 1e-5f, message);
    private static void Require(bool value, string message)
    { if (!value) throw new Exception("Variant check: " + message); }
}
