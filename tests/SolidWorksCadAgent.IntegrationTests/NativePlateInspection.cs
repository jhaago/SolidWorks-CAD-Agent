using System;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.TestUtilities;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.IntegrationTests
{
    internal static class NativePlateInspection
    {
        // Runs exclusively on ISolidWorksSession's STA. Explicit COM interfaces
        // ensure PartDoc operations do not rely on ModelDoc2's default dispatch interface.
        internal static AcceptancePlateEvidence Inspect(object application, JObject bounds)
        {
#if SOLIDWORKS_INTEROP
            var model = (application as SldWorks)?.ActiveDoc as ModelDoc2;
            if (model == null) throw new InvalidOperationException("No active acceptance plate.");
            var part = model as PartDoc;
            if (part == null) throw new InvalidOperationException("The acceptance document must be a native part.");
            var bodies = part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[];
            if (bodies == null || bodies.Length != 1)
                throw new InvalidOperationException("The acceptance plate must have exactly one solid body.");
            var body = bodies[0] as Body2;
            if (body == null) throw new InvalidOperationException("Unexpected native solid body.");
            var mass = body.GetMassProperties(1.0) as double[];
            if (mass == null || mass.Length < 4) throw new InvalidOperationException("Missing body mass properties.");
            var result = new AcceptancePlateEvidence
            {
                MinMm = new[] { bounds.Value<double>("minXmm"), bounds.Value<double>("minYmm"), bounds.Value<double>("minZmm") },
                MaxMm = new[] { bounds.Value<double>("maxXmm"), bounds.Value<double>("maxYmm"), bounds.Value<double>("maxZmm") },
                VolumeMm3 = mass[3] * 1e9 // SOLIDWORKS cubic metres -> cubic millimetres.
            };
            var faces = body.GetFaces() as object[];
            if (faces == null) throw new InvalidOperationException("Missing plate faces.");
            result.FaceCount = faces.Length;
            foreach (var faceObject in faces)
            {
                var face = faceObject as Face2;
                var surface = face?.GetSurface() as Surface;
                if (surface == null) throw new InvalidOperationException("Missing native face surface.");
                if (surface.IsCylinder())
                {
                    var cylinder = surface.CylinderParams as double[];
                    if (cylinder == null || cylinder.Length < 7) throw new InvalidOperationException("Missing cylindrical surface parameters.");
                    result.CylindricalFaceCount++;
                    result.CylindricalRadiusMm = cylinder[6] * 1000;
                    result.CylindricalAreaMm2 += (double)face.GetArea() * 1e6;
                }
                if (!surface.IsPlane()) continue;
                var loops = face.GetLoops() as object[];
                foreach (var loopObject in loops ?? Array.Empty<object>())
                {
                    var loop = loopObject as Loop2;
                    if (loop == null) throw new InvalidOperationException("Unexpected native face loop.");
                    if (loop.IsOuter()) continue;
                    var edges = loop.GetEdges() as object[];
                    // A single closed circular inner loop is an actual opening in a
                    // planar face, rather than a circle merely present in a sketch.
                    if (edges == null || edges.Length != 1) continue;
                    var curve = (edges[0] as Edge)?.GetCurve() as Curve;
                    if (curve == null || !curve.IsCircle()) continue;
                    var circle = curve.CircleParams as double[];
                    if (circle == null || circle.Length < 7) throw new InvalidOperationException("Missing circular edge parameters.");
                    result.Openings.Add(new CircularOpeningEvidence
                    {
                        CentreMm = new[] { circle[0] * 1000, circle[1] * 1000, circle[2] * 1000 },
                        Normal = new[] { circle[3], circle[4], circle[5] },
                        RadiusMm = circle[6] * 1000
                    });
                }
            }
            return result;
#else
            throw new InvalidOperationException("Native acceptance inspection requires a build with SOLIDWORKS interop assemblies.");
#endif
        }
    }
}
