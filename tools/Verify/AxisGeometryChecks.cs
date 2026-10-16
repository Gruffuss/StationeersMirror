using System.Numerics;
using Mono.Cecil;
using StationeersMirror;

internal static class AxisGeometryChecks
{
    internal static void Run(ModuleDefinition mod)
    {
        int cases = 0;
        var root = Matrix4x4.CreateFromYawPitchRoll(.4f, -.8f, .2f) * Matrix4x4.CreateTranslation(2, -3, 5);
        foreach (byte axis in new byte[] { 1, 2, 3 })
        {
            var mirror = ControlTransformMath.Mirror(axis);
            Require(mirror.GetDeterminant() == -1 && ControlTransformMath.Near(mirror * mirror, Matrix4x4.Identity), "each reflection is an involution");
            // Integer cell centres, blocking cells, bound centres, arrow offsets,
            // wireframe endpoints and mesh points all share component reflection.
            for (int x = -2; x <= 2; x++)
            for (int y = -2; y <= 2; y++)
            for (int z = -2; z <= 2; z++)
            {
                var point = new Vector3(x, y, z);
                var expected = point; expected[axis - 1] *= -1;
                var reflected = ControlTransformMath.Reflect(point, axis);
                Near(reflected, expected, "grid/blocking/point component");
                Near(Vector3.Transform(point, mirror * root), Vector3.Transform(reflected, root), "debug expected world cell matches root reflection");
                Near(ControlTransformMath.Reflect(reflected, axis), point, "wireframe/grid undo");
                foreach (byte next in new byte[] { 1, 2, 3 })
                    Near(ControlTransformMath.Reflect(ControlTransformMath.Reflect(reflected, axis), next),
                        ControlTransformMath.Reflect(point, next), "cached cursor restores old axis before applying new");
                cases++;
            }
            var center = new Vector3(.2f, -.3f, .4f);
            var extents = new Vector3(.7f, .8f, .9f);
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                var corner = center + extents * new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
                var reflected = ControlTransformMath.Reflect(corner, axis);
                min = Vector3.Min(min, reflected); max = Vector3.Max(max, reflected);
            }
            Near((min + max) / 2, ControlTransformMath.Reflect(center, axis), "both grid bounds/Thing/blueprint centre");
            Near((max - min) / 2, extents, "bounds sizes unchanged");
            var a = new Vector3(1, 2, 3); var b = new Vector3(-2, 1, 4); var c = new Vector3(.3f, -.4f, .5f);
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var tangent = Vector3.Normalize(b - a);
            var ar = ControlTransformMath.Reflect(a, axis); var br = ControlTransformMath.Reflect(b, axis); var cr = ControlTransformMath.Reflect(c, axis);
            Near(Vector3.Normalize(Vector3.Cross(cr - ar, br - ar)), ControlTransformMath.Reflect(normal, axis), "reversed triangle winding agrees with reflected normal");
            Near(-Vector3.Cross(ControlTransformMath.Reflect(normal, axis), ControlTransformMath.Reflect(tangent, axis)),
                ControlTransformMath.Reflect(Vector3.Cross(normal, tangent), axis), "reflected tangent w reverses handedness");
            Near(Vector3.Transform(ar, mirror * mirror), ar, "ghost counter-scale plus root flip preserves reflected mesh geometry");
            Require((mirror * mirror).GetDeterminant() > 0, "ghost renderer proper world determinant");
        }
        // Connect the shared numerical tests to the Unity/game consumers in the DLL.
        MethodDefinition Method(string type, string name) => mod.Types.Single(t => t.Name == type).Methods.Single(m => m.Name == name);
        bool Calls(MethodDefinition method, string name) => method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == name);
        Require(Calls(Method("MirrorUtil", "Reflect"), "Reflect") && Calls(Method("MirrorUtil", "ReflectDirection"), "Reflect"), "Unity vectors/directions use axis reflection");
        foreach (string name in new[] { "Apply", "MirrorBounds" })
            Require(Calls(Method("MirrorUtil", name), "Reflect"), name + " reflects geometry");
        Require(Method("MirrorUtil", "ReflectGrid").Parameters[1].ParameterType.MetadataType == MetadataType.Byte, "integer grids take axis");
        Require(Calls(Method("CursorMirror", "ReflectWireframe"), "Reflect") &&
            Method("CursorMirror", "ReflectedCopy").Parameters[1].ParameterType.MetadataType == MetadataType.Byte, "wireframe and blueprint mesh take axis");
        foreach (string type in new[] { "MirrorDrivenControls", "MirrorAnimatorControls", "MirrorControlTriggers" })
            Require(mod.Types.Single(t => t.Name == type).Methods.Where(m => m.HasBody).Any(m => Calls(m, "Mirror")), type + " uses Mirror(axis)");
        Require(Calls(Method("MirrorUtil", "CounterFlipAxis"), "AlignedAxis") && Calls(Method("MirrorOccupants", "Compensate"), "CounterFlipAxis"), "aligned text/occupant counter-flips");
        Require(Calls(Method("InteractableSelectionPatch", "Postfix"), "Axis"), "selection uses stored root axis");
        Require(!mod.Types.Single(t => t.Name == "MirrorVariant").Methods.Any(m => m.Name is "ExtraTurn" or "FromTurnAxis"), "no turn semantics remain");
        Console.WriteLine($"PASS axis geometry: {cases} grid/blocking/wireframe points, bounds, mesh normals/tangents/winding, cached-axis transitions, ghost determinant; consumer IL integration.");
    }
    private static void Near(Vector3 a, Vector3 b, string message) => Require(Vector3.Distance(a, b) < 1e-5f, message);
    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception("Axis geometry: " + message); }
}
