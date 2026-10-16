using System;
using System.Numerics;

namespace StationeersMirror;

// System.Numerics uses row vectors: products below are transposes of PLAN.md.
// Shared verbatim with Verify; Unity conversion lives in MirrorControlGeometry.
internal readonly struct ControlPose
{
    internal readonly Vector3 Position, Scale;
    internal readonly Quaternion Rotation;
    internal ControlPose(Vector3 position, Quaternion rotation, Vector3 scale)
    { Position = position; Rotation = rotation; Scale = scale; }
    internal Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) *
        Matrix4x4.CreateTranslation(Position);
}

internal static class ControlTransformMath
{
    internal static Matrix4x4 Mirror(byte axis) => Matrix4x4.CreateScale(Reflect(Vector3.One, axis));
    internal static Vector3 Reflect(Vector3 value, byte axis) => new(
        axis == MirrorVariant.X ? -value.X : value.X,
        axis == MirrorVariant.Y ? -value.Y : value.Y,
        axis == MirrorVariant.Z ? -value.Z : value.Z);
    internal static Matrix4x4 AroundRest(Vector3 p0, byte axis) =>
        Matrix4x4.CreateTranslation(Reflect(p0, axis) - p0);

    // Same, plus a root-local 180-degree turn about axis turn (1 = X, 2 = Y, 3 = Z; 0 = none),
    // applied around the rest pivot: row form T(-p0) * E * T(M p0) = column T(M p0) E T(-p0).
    internal static Matrix4x4 AroundRest(Vector3 p0, byte axis, byte turn)
    {
        if (turn == 0) return AroundRest(p0, axis);
        var e = turn switch
        {
            1 => new Quaternion(1, 0, 0, 0),
            2 => new Quaternion(0, 1, 0, 0),
            _ => new Quaternion(0, 0, 1, 0)
        };
        return Matrix4x4.CreateTranslation(-p0) * Matrix4x4.CreateFromQuaternion(e) *
            Matrix4x4.CreateTranslation(Reflect(p0, axis));
    }

    internal static bool TrySolve(Matrix4x4 vanillaParent, Matrix4x4 actualParent, Matrix4x4 aroundRest,
        out Matrix4x4 correction)
    {
        correction = default;
        if (!Matrix4x4.Invert(actualParent, out var inverse)) return false;
        correction = vanillaParent * aroundRest * inverse;
        return Finite(correction.GetDeterminant());
    }

    internal static bool TryTrs(Matrix4x4 matrix, out ControlPose pose)
    {
        pose = default;
        var x = new Vector3(matrix.M11, matrix.M12, matrix.M13);
        var y = new Vector3(matrix.M21, matrix.M22, matrix.M23);
        var z = new Vector3(matrix.M31, matrix.M32, matrix.M33);
        var scale = new Vector3(x.Length(), y.Length(), z.Length());
        if (!Finite(scale.X) || !Finite(scale.Y) || !Finite(scale.Z) ||
            scale.X < 1e-6f || scale.Y < 1e-6f || scale.Z < 1e-6f) return false;
        // Canonical TRS decomposition sign, not the structure's reflection axis:
        // the recomposed matrix must match for X, Y and Z mirrors alike.
        if (Vector3.Dot(Vector3.Cross(x, y), z) < 0f) scale.X = -scale.X;
        x /= scale.X; y /= scale.Y; z /= scale.Z;
        if (Math.Abs(Vector3.Dot(x, y)) > 1e-5f || Math.Abs(Vector3.Dot(x, z)) > 1e-5f ||
            Math.Abs(Vector3.Dot(y, z)) > 1e-5f) return false; // Shear cannot be one Unity TRS.
        var rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)));
        pose = new ControlPose(new Vector3(matrix.M41, matrix.M42, matrix.M43), rotation, scale);
        return Near(matrix, pose.Matrix);
    }

    internal static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        var difference = a - b;
        float[] errors = { difference.M11, difference.M12, difference.M13, difference.M14,
            difference.M21, difference.M22, difference.M23, difference.M24,
            difference.M31, difference.M32, difference.M33, difference.M34,
            difference.M41, difference.M42, difference.M43, difference.M44 };
        foreach (float error in errors) if (!Finite(error) || Math.Abs(error) > 0.0001f) return false;
        return true;
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

// Recover unchanged channels from the last vanilla sample. A clip can write only
// position.x or scale.y; retaining corrected values in other channels feeds back.
internal sealed class ControlPoseWriteback
{
    private bool _applied;
    private ControlPose _vanilla, _written;
    internal ControlPose Read(ControlPose observed)
    {
        if (!_applied) return observed;
        return new ControlPose(ReadVector(observed.Position, _written.Position, _vanilla.Position),
            observed.Rotation.Equals(_written.Rotation) ? _vanilla.Rotation : observed.Rotation,
            ReadVector(observed.Scale, _written.Scale, _vanilla.Scale));
    }
    internal void Written(ControlPose vanilla, ControlPose written)
    { _vanilla = vanilla; _written = written; _applied = true; }
    internal ControlPose Restore(ControlPose observed)
    { var vanilla = Read(observed); _applied = false; return vanilla; }
    private static Vector3 ReadVector(Vector3 value, Vector3 written, Vector3 vanilla) => new(
        value.X.Equals(written.X) ? vanilla.X : value.X,
        value.Y.Equals(written.Y) ? vanilla.Y : value.Y,
        value.Z.Equals(written.Z) ? vanilla.Z : value.Z);
}
