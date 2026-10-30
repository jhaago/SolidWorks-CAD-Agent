using System;
using System.Collections.Generic;
using System.Linq;

namespace SolidWorksCadAgent.Core.Geometry
{
    /// <summary>Exact planar evidence, independent of face enumeration order or approximate face boxes.</summary>
    public sealed class PlanarFaceFact
    {
        public string Key { get; set; }
        public Vec3 Normal { get; set; }
        public Vec3 PointMm { get; set; }
        public static PlanarFaceFact FromNative(string key,double[] normal,double[] closestPointXyzUvMetres)
        {
            if(normal==null||normal.Length!=3||closestPointXyzUvMetres==null||closestPointXyzUvMetres.Length!=5||
                normal.Any(v=>!Vec3.Finite(v))||closestPointXyzUvMetres.Any(v=>!Vec3.Finite(v)))
                throw new TopologyQueryException("A planar face needs three finite outward-normal values and five finite closest-point XYZUV values.");
            try{return new PlanarFaceFact{Key=key,Normal=Vec3.From(normal),PointMm=Vec3.From(closestPointXyzUvMetres)*1000};}
            catch(ArgumentException){throw new TopologyQueryException("Native face coordinates are not representable in millimetres.");}
        }
    }
    public static class AxisAlignedFaceSelection
    {
        public const double CoordinateToleranceMm=0.00001;
        public const double NormalTolerance=0.00000001;
        public static PlanarFaceFact Choose(IReadOnlyList<PlanarFaceFact> faces,string axis,string side,double minMm,double maxMm)
        {
            if(axis!="X"&&axis!="Y"&&axis!="Z"||side!="Min"&&side!="Max"||!Vec3.Finite(minMm)||!Vec3.Finite(maxMm)||minMm>=maxMm)
                throw new ArgumentException("An X/Y/Z axis, Min/Max side and finite increasing body bounds are required.");
            if(faces==null||faces.Count==0||faces.Count>20000)throw new TopologyQueryException("Planar face evidence is absent or exceeds the 20000-face budget.");
            var ids=new HashSet<string>(StringComparer.Ordinal);var matches=new List<PlanarFaceFact>();
            var direction=axis=="X"?Vec3.UnitX:axis=="Y"?Vec3.UnitY:Vec3.UnitZ;
            var wanted=side=="Max"?direction:-direction;double target=side=="Max"?maxMm:minMm;
            foreach(var face in faces)
            {
                if(face==null||string.IsNullOrWhiteSpace(face.Key)||face.Key.Length>256||!ids.Add(face.Key)||
                    !Vec3.Finite(face.Normal.Length)||Math.Abs(face.Normal.Length-1)>1e-6)
                    throw new TopologyQueryException("Planar face identities or outward normals are invalid or duplicated.");
                double coordinate=face.PointMm.Dot(direction);
                if(coordinate<minMm-CoordinateToleranceMm||coordinate>maxMm+CoordinateToleranceMm)
                    throw new TopologyQueryException("A native face point lies outside the measured body extent.");
                var normal=face.Normal.Normalized();
                if(normal.Cross(wanted).Length<=NormalTolerance&&normal.Dot(wanted)>0&&Math.Abs(coordinate-target)<=CoordinateToleranceMm)
                    matches.Add(face);
            }
            if(matches.Count!=1)throw new TopologyQueryException("Expected one outward planar face at the measured body extreme; found "+matches.Count+". Split, tilted or absent faces are not selected by fallback.");
            return matches[0];
        }
    }
}
