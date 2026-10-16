using System.Numerics;
using StationeersMirror;

internal static class AnimatorControlChecks
{
    internal static void Run()
    {
        // Through-panel mirror (owner, 2026-10-16: Larre push button and computer rocker faced into
        // the device with mirror axis Z, their facing): the half-turn about the up axis must put the
        // control on the mirrored face, facing outward, as a proper transform.
        {
            var p0 = new System.Numerics.Vector3(0f, -0.448f, 0.2411f);       // Larre button pivot, logged
            var a = StationeersMirror.ControlTransformMath.AroundRest(p0, 3, 2); // mirror Z, half-turn about Y
            var vanilla = System.Numerics.Matrix4x4.CreateTranslation(p0);       // facing +Z at rest
            var d = vanilla * a;
            var position = new System.Numerics.Vector3(d.M41, d.M42, d.M43);
            var facing = System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitZ, d);
            Require(System.Numerics.Vector3.Distance(position, new System.Numerics.Vector3(0f, -0.448f, -0.2411f)) < 1e-5f, "through-panel control on the mirrored face");
            Require(facing.Z < -0.999f, "through-panel control faces outward (-Z)");
            Require(d.GetDeterminant() > 0f, "through-panel control is a proper transform");
            var plain = vanilla * StationeersMirror.ControlTransformMath.AroundRest(p0, 3);
            Require(System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitZ, plain).Z > 0.999f,
                "without the turn the control faces inward (the bug seen in game)");
        }
        foreach (string word in new[] { "switch", "button", "lever", "knob", "dial", "toggle", "rocker", "onoff" })
            Require(AnimatorControlPolicy.IsControlName("prefix_" + word.ToUpperInvariant() + "_suffix"), "control name " + word);
        foreach (string word in new[] { "door", "panel", "flap", "hatch", "lid", "cover", "drawer", "slide" })
            Require(!AnimatorControlPolicy.IsControlName("Switch_" + word.ToUpperInvariant()), "mechanism veto " + word);
        Require(!AnimatorControlPolicy.IsControlName("ComputerBuildState002"), "no name match");
        VariantChecks.Run();
        var samples = new[]
        {
            new Quaternion(.430459291f, .430459619f, .560985386f, .560985506f),
            new Quaternion(.560985267f, .560985684f, .430459291f, .430459619f)
        };
        var random = new Random(4117);
        int cases = 0;
        foreach (byte axis in new byte[] { 1, 2, 3 })
        foreach (var pose in samples.Concat(Enumerable.Range(0, 100).Select(_ =>
            Quaternion.CreateFromYawPitchRoll((float)random.NextDouble() * 6f,
                (float)random.NextDouble() * 6f, (float)random.NextDouble() * 6f))))
        {
            var pivot = new Vector3(.484f, .445f, .306f);
            var vanilla = new ControlPose(pivot, Quaternion.Normalize(pose), new Vector3(1.3f, .7f, 2));
            var mirror = ControlTransformMath.Mirror(axis);
            var a = ControlTransformMath.AroundRest(pivot, axis);
            Require(ControlTransformMath.TrySolve(Matrix4x4.Identity, mirror, a, out var g) &&
                ControlTransformMath.TryTrs(vanilla.Matrix * g, out _), "direct Animator exact TRS for each mirror axis");
            var root = Matrix4x4.CreateFromYawPitchRoll(.7f, -.4f, .2f);
            foreach (var point in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(2, -3, 1) })
            {
                var actual = Vector3.Transform(point, vanilla.Matrix * g * mirror * root);
                var expected = Vector3.Transform(Vector3.Transform(point, vanilla.Matrix) - pivot + ControlTransformMath.Reflect(pivot, axis), root);
                Require(Vector3.Distance(actual, expected) < 1e-5f, "axis-independent matrix solver preserves vanilla geometry");
                cases++;
            }
        }
        Console.WriteLine($"PASS Animator controls: name policy and {cases} direct-child world geometry cases across X/Y/Z.");
    }
    private static void Require(bool value, string message)
    { if (!value) throw new Exception("Animator control check: " + message); }
}
