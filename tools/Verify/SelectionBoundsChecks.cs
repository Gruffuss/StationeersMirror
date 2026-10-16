using System.Numerics;
using StationeersMirror;

internal static class SelectionBoundsChecks
{
    // Unity Bounds semantics, including signed extents. Setters re-evaluate the
    // opposite endpoint from the current centre/extents, never sort endpoints.
    private struct Bounds
    {
        internal Vector3 Center, Extents;
        internal Vector3 Min { get => Center - Extents; set => SetMinMax(value, Max); }
        internal Vector3 Max { get => Center + Extents; set => SetMinMax(Min, value); }
        internal void SetMinMax(Vector3 min, Vector3 max)
        { Extents = (max-min)*0.5f; Center = min+Extents; }
    }
    private static Bounds Vanilla(Vector3 center, Vector3 extents, Vector3 scale)
    {
        var b = new Bounds { Center=center, Extents=extents };
        var min = b.Min*scale; var max = b.Max*scale;
        b.SetMinMax(min,max);
        b.Min -= new Vector3(.01f);
        b.Max += new Vector3(.01f);
        return b;
    }
    internal static void Run()
    {
        int count=0;
        foreach(float e in new[]{.003f,.01f,.03f,.4f})
        foreach(int axis in new[]{0,1,2})
        foreach(float sign in new[]{-1f,1f})
        {
            var center=new Vector3(.02f,-.04f,.06f);
            var extents=new Vector3(e,e,e);
            var scale=new Vector3(1.2f,-.8f,1.1f); scale[axis]=sign*1.5f;
            var original=Vanilla(center,extents,scale);
            var mirroredScale=scale; mirroredScale[axis]=-scale[axis];
            var mirrored=Vanilla(center,extents,mirroredScale);
            var corrected=mirrored;
            corrected.Extents[axis]=SelectionBoundsMath.RestoreVanillaExtent(mirrored.Extents[axis],mirroredScale[axis]);
            Require(MathF.Abs(corrected.Extents[axis]-MathF.Abs(original.Extents[axis]))<1e-6f,"same vanilla padded magnitude, including authored negative and padding crossing zero");
            for(int j=0;j<3;j++) if(j!=axis)
                Require(corrected.Extents[j]==mirrored.Extents[j],"other axes unchanged, even negative");
            var reflectedCenter=original.Center; reflectedCenter[axis]=-reflectedCenter[axis];
            Require(Vector3.Distance(corrected.Center,reflectedCenter)<1e-6f,"centre equals aligned reflected vanilla centre");
            count++;
        }
        foreach(byte mirrorAxis in new byte[]{1,2,3})
        foreach(var rotation in new[]{Quaternion.Identity,Quaternion.CreateFromAxisAngle(Vector3.UnitZ,MathF.PI/2),Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2)})
        {
            var rootAxis=Vector3.Zero; rootAxis[mirrorAxis-1]=1;
            var local=Vector3.Transform(rootAxis,Quaternion.Inverse(rotation));
            int actual=SelectionBoundsMath.AlignedAxis(local.X,local.Y,local.Z);
            Require(actual>=0,"rotated collider aligned to each root axis");
            var scale=Vector3.One; scale[actual]=-1;
            var localMirror=Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(rotation)) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
            Require(ControlTransformMath.Near(localMirror,ControlTransformMath.Mirror(mirrorAxis)),"mapped collider/text/occupant axis cancels selected root reflection");
        }
        var oblique=Vector3.Normalize(new Vector3(1,1,0));
        Require(SelectionBoundsMath.AlignedAxis(oblique.X,oblique.Y,oblique.Z)==-1,"off-axis left untouched");
        var untouched=Vanilla(Vector3.One,new Vector3(.04f),new Vector3(-1,1,1));
        var noMirror=Apply(false,Vector3.UnitX,untouched,new Vector3(-1,1,1));
        var offAxis=Apply(true,oblique,untouched,new Vector3(-1,1,1));
        Require(noMirror.Extents==untouched.Extents && offAxis.Extents==untouched.Extents,"non-mirrored and off-axis unchanged");
        Console.WriteLine($"PASS selection Bounds setter replica: {count} signed/padded extent and centre cases; non-mirrored/off-axis unchanged; collider X/Y/Z alignment.");
    }
    private static Bounds Apply(bool mirrored,Vector3 axis,Bounds b,Vector3 scale)
    {
        int i=SelectionBoundsMath.AlignedAxis(axis.X,axis.Y,axis.Z);
        if(mirrored && i>=0)b.Extents[i]=SelectionBoundsMath.RestoreVanillaExtent(b.Extents[i],scale[i]);
        return b;
    }
    private static void Require(bool condition,string name){if(!condition)throw new Exception("Selection check: "+name);}
}
