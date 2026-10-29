// Adapted from soheil-mp/solidworks-mcp, f450dab, Geometry/Vec3.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: net48 struct, finite/stable normalization.
using System;

namespace SolidWorksCadAgent.Core.Geometry
{
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public Vec3(double x,double y,double z)
        {
            if(!Finite(x)||!Finite(y)||!Finite(z))throw new ArgumentOutOfRangeException(nameof(x),"Vector components must be finite.");
            X=x;Y=y;Z=z;
        }
        public double X { get; } public double Y { get; } public double Z { get; }
        public static readonly Vec3 Zero=new Vec3(0,0,0),UnitX=new Vec3(1,0,0),UnitY=new Vec3(0,1,0),UnitZ=new Vec3(0,0,1);
        public static Vec3 operator +(Vec3 a,Vec3 b)=>new Vec3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Vec3 operator -(Vec3 a,Vec3 b)=>new Vec3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Vec3 operator -(Vec3 a)=>new Vec3(-a.X,-a.Y,-a.Z);
        public static Vec3 operator *(Vec3 a,double s)=>new Vec3(a.X*s,a.Y*s,a.Z*s);
        public static Vec3 operator *(double s,Vec3 a)=>a*s;
        public static Vec3 operator /(Vec3 a,double s)=>new Vec3(a.X/s,a.Y/s,a.Z/s);
        public double Dot(Vec3 b)=>X*b.X+Y*b.Y+Z*b.Z;
        public Vec3 Cross(Vec3 b)=>new Vec3(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public double Length
        {
            get { double scale=Math.Max(Math.Abs(X),Math.Max(Math.Abs(Y),Math.Abs(Z)));
                if(scale==0)return 0;var v=this/scale;return scale*Math.Sqrt(v.Dot(v)); }
        }
        public double DistanceTo(Vec3 b)=>(this-b).Length;
        public Vec3 Normalized()
        {
            double scale=Math.Max(Math.Abs(X),Math.Max(Math.Abs(Y),Math.Abs(Z)));
            if(scale==0)throw new ArgumentException("A direction cannot be zero.");
            var v=this/scale;return v/Math.Sqrt(v.Dot(v));
        }
        public bool IsParallelTo(Vec3 b,double maxAngleRad=1e-6)
        {
            if(!Finite(maxAngleRad)||maxAngleRad<0||maxAngleRad>Math.PI/2)throw new ArgumentOutOfRangeException(nameof(maxAngleRad));
            return Math.Abs(Normalized().Dot(b.Normalized()))>=Math.Cos(maxAngleRad)-1e-15;
        }
        public double[] ToArray()=>new[]{X,Y,Z};
        public static Vec3 From(double[] a,int offset=0)
        {
            if(a==null||offset<0||offset>a.Length-3)throw new ArgumentException("Three finite vector components required.");
            return new Vec3(a[offset],a[offset+1],a[offset+2]);
        }
        public bool Equals(Vec3 other)=>X.Equals(other.X)&&Y.Equals(other.Y)&&Z.Equals(other.Z);
        public override bool Equals(object other)=>other is Vec3 v&&Equals(v);
        public override int GetHashCode()=>unchecked((X.GetHashCode()*397^Y.GetHashCode())*397^Z.GetHashCode());
        public override string ToString()=>string.Format(System.Globalization.CultureInfo.InvariantCulture,"({0:G6}, {1:G6}, {2:G6})",X,Y,Z);
        public static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v);
    }
}
