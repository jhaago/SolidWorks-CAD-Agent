// Adapted from soheil-mp/solidworks-mcp, f450dab, Geometry/Xform.cs (MIT).
// Copyright (c) 2026 Soheil and the solidworks-mcp contributors.
// See third_party/solidworks-mcp/LICENSE. Changes: net48 arrays; validate rigid/scaled transforms.
using System;
using System.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    /// <summary>SOLIDWORKS ArrayData uses row vectors: p' = s * (p * R) + t.</summary>
    public sealed class Xform
    {
        private readonly double[] _r;
        private readonly Vec3 _t;
        private readonly double _s;
        public Xform(double[] data)
        {
            if(data==null||data.Length!=16||data.Any(v=>!Vec3.Finite(v))||data[12]<=0)
                throw new ArgumentException("A finite 16-value transform with positive scale is required.",nameof(data));
            _r=data.Take(9).ToArray();_t=Vec3.From(data,9);_s=data[12];
            var rows=new[]{Vec3.From(_r),Vec3.From(_r,3),Vec3.From(_r,6)};
            for(int i=0;i<3;i++)for(int j=0;j<3;j++)
                if(Math.Abs(rows[i].Dot(rows[j])-(i==j?1:0))>1e-9)
                    throw new ArgumentException("Transform rotation must be orthonormal; shear/singular matrices are unsupported.",nameof(data));
        }
        private Xform(double[] r,Vec3 t,double scale){_r=(double[])r.Clone();_t=t;_s=scale;}
        public static Xform Identity { get; }=new Xform(new double[]{1,0,0,0,1,0,0,0,1,0,0,0,1,0,0,0});
        public Vec3 Translation=>_t;
        private Vec3 Rotate(Vec3 p)=>new Vec3(p.X*_r[0]+p.Y*_r[3]+p.Z*_r[6],p.X*_r[1]+p.Y*_r[4]+p.Z*_r[7],p.X*_r[2]+p.Y*_r[5]+p.Z*_r[8]);
        public Vec3 ApplyPoint(Vec3 p)=>Rotate(p)*_s+_t;
        public Vec3 ApplyVector(Vec3 v)=>Rotate(v);
        public Xform Inverse()
        {
            double[] rt={_r[0],_r[3],_r[6],_r[1],_r[4],_r[7],_r[2],_r[5],_r[8]};
            double si=1/_s;if(!Vec3.Finite(si))throw new InvalidOperationException("Inverse scale is not representable.");
            var inv=new Xform(rt,Vec3.Zero,si);return new Xform(rt,-(inv.Rotate(_t)*si),si);
        }
        public double[] ToArrayData()=>_r.Concat(new[]{_t.X,_t.Y,_t.Z,_s,0,0,0}).ToArray();
        public bool IsIdentity(double tol=1e-12)=>
            Math.Abs(_r[0]-1)<tol&&Math.Abs(_r[4]-1)<tol&&Math.Abs(_r[8]-1)<tol&&
            Math.Abs(_r[1])<tol&&Math.Abs(_r[2])<tol&&Math.Abs(_r[3])<tol&&Math.Abs(_r[5])<tol&&Math.Abs(_r[6])<tol&&Math.Abs(_r[7])<tol&&_t.Length<tol&&Math.Abs(_s-1)<tol;
    }
}
