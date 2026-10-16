using System.Numerics;
using StationeersMirror;

internal static class ControlMathChecks
{
    internal static void Run()
    {
        foreach (string action in new[] { "OnOff", "Mode", "Activate" }.Concat(Enumerable.Range(1, 17).Select(n => "Button" + n)))
        {
            Require(ControlPolicy.Classify(action) == PartKind.Control, "control action");
            Require(ControlPolicy.Classify(action, true) == PartKind.Mechanism, "mechanism override");
        }
        foreach (string action in new[] { "Open", "Lock" }) Require(ControlPolicy.Classify(action) == PartKind.Mechanism, action);
        Require(ControlPolicy.Classify("FutureAction") == PartKind.Ambiguous, "unknown action");
        var rng = new Random(51);
        Quaternion RandomRotation() => Quaternion.CreateFromYawPitchRoll((float)rng.NextDouble()*6, (float)rng.NextDouble()*6, (float)rng.NextDouble()*6);
        int cases = 0;
        for (int i = 0; i < 100; i++)
        {
            var p = new ControlPose(new Vector3(1, 2, -3), RandomRotation(), new Vector3(1.3f)).Matrix *
                new ControlPose(new Vector3(-2, 1, 3), RandomRotation(), new Vector3(0.8f)).Matrix;
            var rest = new Vector3(0.4f, -0.3f, 0.2f);
            var p0 = Vector3.Transform(rest, p);
            foreach (byte axis in new byte[] { 1, 2, 3 })
            {
                var a = ControlTransformMath.AroundRest(p0, axis);
                var actualParent = p * ControlTransformMath.Mirror(axis);
                Require(ControlTransformMath.TrySolve(p, actualParent, a, out var g), "solve G");
                Require(ControlTransformMath.TryTrs(g, out var proxy), "constant improper proxy has exact TRS");
                Require(proxy.Scale.X < 0 && proxy.Scale.Y > 0 && proxy.Scale.Z > 0, "reflection isolated in X scale");
                foreach (float travel in new[] { 0f, 0.03f, 0.1f })
                {
                    var l = new ControlPose(rest + new Vector3(travel, -travel, travel), RandomRotation(), new Vector3(1.1f, 0.7f, 1.2f));
                    var desired = l.Matrix * p * a;
                    var actual = l.Matrix * proxy.Matrix * actualParent;
                    Near(actual, desired, "script absolute pose gives D");
                    Require(desired.GetDeterminant() > 0, "D is proper");
                    if (travel == 0f) Near(Vector3.Transform(Vector3.Zero, desired), ControlTransformMath.Reflect(p0, axis), "rest position M_a p0");
                    // Same equation for Animator local pose, including translation.
                    Require(ControlTransformMath.TryTrs(l.Matrix * g, out var written), "nested Animator local TRS");
                    Near(written.Matrix * actualParent, desired, "nested Animator gives D");
                    cases++;
                }
                // Nested corrected Animator ancestor is already proper; solve
                // against its actual matrix, not an assumed uncompensated parent.
                var outerA = ControlTransformMath.AroundRest(new Vector3(2,-1,3), (byte)(axis % 3 + 1));
                Require(ControlTransformMath.TrySolve(p, p * outerA, a, out var nested), "nested corrected parent solve");
                Near(nested * p * outerA, p * a, "nested corrected parent frame");
            }
        }
        foreach (byte mirrorAxis in new byte[] { 1, 2, 3 })
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var p0 = new Vector3(0.484f, 0.445f, 0.306f);
            var a = ControlTransformMath.AroundRest(p0, mirrorAxis);
            foreach (float angle in new[] { -13.5f, 13.5f })
            {
                var l = new ControlPose(p0, Quaternion.CreateFromAxisAngle(axis, angle*MathF.PI/180), Vector3.One);
                var d = l.Matrix * a;
                foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                    Near(Vector3.TransformNormal(v, d), Vector3.TransformNormal(v, l.Matrix), "X/Y hinge and front knob motion identical to vanilla");
            }
            var first = Vector3.Normalize(Vector3.Cross(axis, axis.X == 1 ? Vector3.UnitY : Vector3.UnitX));
            var next = Vector3.Transform(first, Quaternion.CreateFromAxisAngle(axis, 0.1f));
            Require(Vector3.Dot(Vector3.Cross(first,next), axis)>0, "vanilla positive motion sense retained");
        }
        // With E=identity, all normals and local button/rotary motion remain vanilla.
        foreach (byte mirrorAxis in new byte[] { 1, 2, 3 })
        foreach (var direction in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        foreach (float sign in new[] { -1f, 1f })
        {
            var normal = direction * sign;
            var p0 = normal * 0.5f;
            var a = ControlTransformMath.AroundRest(p0, mirrorAxis);
            var face = Vector3.TransformNormal(normal, a);
            Near(face, normal, "face keeps vanilla orientation");
            var inward = -normal * 0.03f;
            var pressed = Vector3.Transform(p0 + inward, a);
            var pivot = ControlTransformMath.Reflect(p0, mirrorAxis);
            Near(pressed - pivot, inward, "button travel is vanilla");
            Require(Vector3.Dot(pressed - pivot, normal) < 0, "button press inward relative to vanilla face");
            var radial = direction == Vector3.UnitX ? Vector3.UnitZ : Vector3.UnitX;
            var next = Vector3.Transform(radial, Quaternion.CreateFromAxisAngle(normal, 0.1f));
            Require(Vector3.Dot(Vector3.Cross(Vector3.TransformNormal(radial,a),Vector3.TransformNormal(next,a)),face)>0,
                "same signed turn seen outside vanilla face");
        }
        foreach (byte mirrorAxis in new byte[] { 1, 2, 3 })
        {
            var mirror = ControlTransformMath.Mirror(mirrorAxis);
            var pShear = Matrix4x4.CreateScale(2, 3, 1) * Matrix4x4.CreateFromYawPitchRoll(.6f, .4f, .2f);
            ControlTransformMath.TrySolve(pShear, pShear * mirror, Matrix4x4.Identity, out var shear);
            Require(!ControlTransformMath.TryTrs(shear, out _), "sheared proxy rejected instead of approximated");
            var fixedP = Matrix4x4.CreateTranslation(0.3f,0.2f,0.1f);
            var restA = ControlTransformMath.AroundRest(new Vector3(0.4f,0.2f,0.1f),mirrorAxis);
            ControlTransformMath.TrySolve(fixedP, fixedP*mirror, restA,out var fixedG);
            var movingP = Matrix4x4.CreateFromYawPitchRoll(.6f, .4f, .2f)*fixedP;
            Require(!ControlTransformMath.Near(fixedG*movingP*mirror,movingP*restA),
                "negative control: constant G cannot generally follow independently moving ancestors");
            ControlTransformMath.TrySolve(movingP,movingP*mirror,restA,out var dynamicG);
            Near(dynamicG*movingP*mirror,movingP*restA,"Animator recomputation handles moving ancestors");
        }
        CheckWriteback();
        var recovery = new MirrorRecovery();
        var restored = new List<int>();
        recovery.Try(()=>restored.Add(1)); recovery.Try(()=>throw new Exception("injected")); recovery.Try(()=>restored.Add(3));
        Require(restored.SequenceEqual(new[]{1,3}),"rollback continues after failure");
        Console.WriteLine($"PASS unified rest-pivot D: {cases} nested script/Animator cases across X/Y/Z, proper geometry, vanilla motion, moving Animator ancestors, shear rejection, partial-channel/idle/undo state, recovery.");
    }
    private static void CheckWriteback()
    {
        var state = new ControlPoseWriteback();
        var raw = new ControlPose(new Vector3(1,2,3),Quaternion.CreateFromYawPitchRoll(.3f,.2f,.1f),new Vector3(1,2,3));
        var written = new ControlPose(new Vector3(-2,-3,-4),Quaternion.CreateFromYawPitchRoll(-.3f,-.2f,-.1f),new Vector3(-1,2,3));
        state.Written(raw,written);
        for(int i=0;i<120;i++) Near(state.Read(written).Matrix,raw.Matrix,"idle recovers raw without feedback");
        var partial = new ControlPose(new Vector3(5,written.Position.Y,written.Position.Z),written.Rotation,new Vector3(written.Scale.X,7,written.Scale.Z));
        var expected = new ControlPose(new Vector3(5,2,3),raw.Rotation,new Vector3(1,7,3));
        Near(state.Read(partial).Matrix,expected.Matrix,"partial position and scale writes preserve other vanilla channels");
        Near(state.Restore(partial).Matrix,expected.Matrix,"undo includes latest partial animation");
        Near(state.Read(raw).Matrix,raw.Matrix,"unapplied state reads raw");
    }
    private static void Near(Vector3 a,Vector3 b,string name)=>Require(Vector3.Distance(a,b)<0.0001f,name);
    private static void Near(Matrix4x4 a,Matrix4x4 b,string name)=>Require(ControlTransformMath.Near(a,b),name);
    private static void Require(bool value,string name){if(!value)throw new Exception("Control check: "+name);}
}
