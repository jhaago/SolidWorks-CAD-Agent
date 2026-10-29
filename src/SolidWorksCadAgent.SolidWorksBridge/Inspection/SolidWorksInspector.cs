using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    public sealed class FeatureInspectionItem
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public int ErrorCode { get; set; }
        public bool IsWarning { get; set; }
        public string ParentName { get; set; }
        public int Depth { get; set; }
    }

    public sealed class RebuildInspectionResult
    {
        public bool Rebuilt { get; set; }
        public bool HasErrors { get; set; }
        public bool HasWarnings { get; set; }
        public IReadOnlyList<FeatureInspectionItem> Features { get; set; }
    }

#if SOLIDWORKS_INTEROP
    internal static class SolidWorksInspector
    {
        public static object[] GetSolidBodies(ModelDoc2 model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var part = model as PartDoc;
            if (part == null)
            {
                throw new InvalidOperationException("The active SOLIDWORKS document is not a part document.");
            }

            // Visibility is presentation state; physical verification includes hidden solids.
            return part.GetBodies2((int)swBodyType_e.swSolidBody, false) as object[] ?? Array.Empty<object>();
        }

        public static PreciseBoundingBox GetPreciseBoundingBox(ModelDoc2 model)
        {
            var bodies = GetSolidBodies(model);
            if (bodies.Length == 0)
            {
                throw new InvalidOperationException("The active part contains no solid bodies.");
            }

            var accumulator = new PreciseBoundsAccumulator();
            foreach (var bodyObject in bodies)
            {
                var body = bodyObject as Body2;
                if (body == null)
                {
                    throw new InvalidOperationException("SOLIDWORKS returned an unexpected solid-body object.");
                }

                double minX, minY, minZ;
                double maxX, maxY, maxZ;
                double unused1, unused2;

                if (!body.GetExtremePoint(-1.0, 0.0, 0.0, out minX, out unused1, out unused2))
                    throw new InvalidOperationException("SOLIDWORKS could not calculate the minimum X extent of a body.");
                if (!body.GetExtremePoint(1.0, 0.0, 0.0, out maxX, out unused1, out unused2))
                    throw new InvalidOperationException("SOLIDWORKS could not calculate the maximum X extent of a body.");
                if (!body.GetExtremePoint(0.0, -1.0, 0.0, out unused1, out minY, out unused2))
                    throw new InvalidOperationException("SOLIDWORKS could not calculate the minimum Y extent of a body.");
                if (!body.GetExtremePoint(0.0, 1.0, 0.0, out unused1, out maxY, out unused2))
                    throw new InvalidOperationException("SOLIDWORKS could not calculate the maximum Y extent of a body.");
                if (!body.GetExtremePoint(0.0, 0.0, -1.0, out unused1, out unused2, out minZ))
                    throw new InvalidOperationException("SOLIDWORKS could not calculate the minimum Z extent of a body.");
                if (!body.GetExtremePoint(0.0, 0.0, 1.0, out unused1, out unused2, out maxZ))
                    throw new InvalidOperationException("SOLIDWORKS could not calculate the maximum Z extent of a body.");

                accumulator.IncludeBodyExtentsMetres(minX, minY, minZ, maxX, maxY, maxZ);
            }

            return accumulator.ToMillimetres();
        }

        public static List<FeatureInspectionItem> GetFeatureTree(ModelDoc2 model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var entries = BoundedFeatureTraversal.Walk(RootFeatures(model),
                feature => feature.GetID().ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + feature.GetTypeName2() + ":" + feature.Name,
                SubFeatures);
            var result = new List<FeatureInspectionItem>();
            foreach (var entry in entries)
            {
                var feature = entry.Node;
                bool isWarning;
                var errorCode = feature.GetErrorCode2(out isWarning);
                var typeName = feature.GetTypeName2();
                // Instant3D wraps features as ICE; GetTypeName exposes the underlying native type.
                if (typeName == "ICE")
                {
                    typeName = feature.GetTypeName();
                }
                result.Add(new FeatureInspectionItem
                {
                    Name = feature.Name,
                    TypeName = typeName,
                    ParentName = entry.Parent?.Name,
                    Depth = entry.Depth,
                    ErrorCode = errorCode,
                    IsWarning = errorCode != 0 && isWarning
                });
            }

            return result;
        }

        private static IEnumerable<Feature> RootFeatures(ModelDoc2 model)
        {
            for (var feature = model.IFirstFeature(); feature != null; feature = feature.GetNextFeature() as Feature)
                yield return feature;
        }

        private static IEnumerable<Feature> SubFeatures(Feature parent)
        {
            for (var feature = parent.GetFirstSubFeature() as Feature; feature != null; feature = feature.GetNextSubFeature() as Feature)
                yield return feature;
        }

        public static JArray GetPrismaticCutReadback(ModelDoc2 model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var cuts = new JArray();
            Feature feature = model.IFirstFeature();
            while (feature != null)
            {
                string nativeType = feature.GetTypeName2();
                if (nativeType == "ICE") nativeType = feature.GetTypeName();
                if (string.Equals(nativeType, "Cut", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nativeType, "CutExtrude", StringComparison.OrdinalIgnoreCase))
                    cuts.Add(ReadCutFeature(model, feature));
                feature = feature.GetNextFeature() as Feature;
            }
            return cuts;
        }

        private static JObject ReadCutFeature(ModelDoc2 model, Feature feature)
        {
            var definition = feature.GetDefinition() as ExtrudeFeatureData2;
            if (definition == null || definition.IsBossFeature())
                throw new InvalidOperationException("A native cut feature did not expose its SOLIDWORKS extrusion definition.");
            if (!definition.AccessSelections(model, null))
                throw new InvalidOperationException("SOLIDWORKS could not expose the native cut's definition selections.");
            string endConditionName;
            double depthMm;
            bool reverseDirection, flipSideToCut, bothDirections;
            try
            {
                int endCondition = definition.GetEndCondition(true);
                if (endCondition == (int)swEndConditions_e.swEndCondBlind) endConditionName = "Blind";
                else if (endCondition == (int)swEndConditions_e.swEndCondThroughAll) endConditionName = "ThroughAll";
                else throw new InvalidOperationException("A native cut has an unsupported end condition.");
                depthMm = definition.GetDepth(true) * 1000.0;
                reverseDirection = definition.ReverseDirection;
                flipSideToCut = definition.FlipSideToCut;
                bothDirections = definition.BothDirections;
            }
            finally { definition.ReleaseSelectionAccess(); }
            bool isBlind = endConditionName == "Blind";
            if (isBlind && (double.IsNaN(depthMm) || double.IsInfinity(depthMm) || depthMm <= 0))
            {
                // The native forward depth is also exposed as the feature's D1 dimension.
                var dimension = FindFeatureDimension(model, feature, "D1");
                if (dimension != null) depthMm = dimension.SystemValue * 1000.0;
                if (double.IsNaN(depthMm) || double.IsInfinity(depthMm) || depthMm <= 0)
                    throw new InvalidOperationException("A native blind cut has no finite positive depth (feature=" + feature.Name +
                        ", featureDataMm=" + (definition.GetDepth(true) * 1000.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                        ", D1mm=" + (dimension == null ? "unavailable" : (dimension.SystemValue * 1000.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture)) + ").");
            }

            var parents = feature.GetParents() as object[] ?? Array.Empty<object>();
            var sketchFeature = parents.OfType<Feature>().FirstOrDefault(parent => parent.GetTypeName2() == "ProfileFeature");
            var sketch = sketchFeature?.GetSpecificFeature2() as Sketch;
            if (sketchFeature == null || sketch == null)
                throw new InvalidOperationException("A native cut's direct parent profile sketch could not be identified.");
            return new JObject
            {
                ["featureName"] = feature.Name,
                ["endCondition"] = endConditionName,
                ["depthMm"] = isBlind ? new JValue(depthMm) : JValue.CreateNull(),
                ["reverseDirection"] = reverseDirection,
                ["flipSideToCut"] = flipSideToCut,
                ["bothDirections"] = bothDirections,
                ["profile"] = ReadProfileSketch(sketchFeature.Name, sketch)
            };
        }

        private static Dimension FindFeatureDimension(ModelDoc2 model, Feature feature, string dimensionName)
        {
            var dimension = feature.IParameter(dimensionName) as Dimension ?? feature.Parameter(dimensionName) as Dimension;
            if (dimension != null) return dimension;
            dimension = model.IParameter(dimensionName + "@" + feature.Name) as Dimension ??
                model.Parameter(dimensionName + "@" + feature.Name) as Dimension;
            if (dimension != null) return dimension;
            var display = feature.GetFirstDisplayDimension() as DisplayDimension;
            while (display != null)
            {
                dimension = display.GetDimension2(0) as Dimension;
                if (dimension != null && string.Equals(dimension.Name, dimensionName, StringComparison.OrdinalIgnoreCase)) return dimension;
                display = feature.GetNextDisplayDimension(display) as DisplayDimension;
            }
            return null;
        }

        private static JObject ReadProfileSketch(string sketchName, Sketch sketch)
        {
            var segments = (sketch.GetSketchSegments() as object[] ?? Array.Empty<object>())
                .OfType<SketchSegment>().Where(segment => !segment.ConstructionGeometry).ToArray();
            int lines = 0, arcs = 0;
            var points = new List<Tuple<double, double>>();
            var nativeSegments = new JArray();
            foreach (var segment in segments)
            {
                int type = segment.GetType();
                if (type == (int)swSketchSegments_e.swSketchLINE)
                {
                    var line = segment as SketchLine;
                    if (line == null) throw new InvalidOperationException("A native line segment could not be read.");
                    var start = (SketchPoint)line.GetStartPoint2(); var end = (SketchPoint)line.GetEndPoint2();
                    double x1 = start.X * 1000.0, y1 = start.Y * 1000.0;
                    double x2 = end.X * 1000.0, y2 = end.Y * 1000.0;
                    Include(points, x1, y1); Include(points, x2, y2);
                    nativeSegments.Add(new JObject { ["type"] = "Line", ["startXmm"] = x1, ["startYmm"] = y1,
                        ["endXmm"] = x2, ["endYmm"] = y2 });
                    lines++;
                }
                else if (type == (int)swSketchSegments_e.swSketchARC)
                {
                    var arc = segment as SketchArc;
                    if (arc == null) throw new InvalidOperationException("A native arc segment could not be read.");
                    var center = (SketchPoint)arc.GetCenterPoint2();
                    var start = (SketchPoint)arc.GetStartPoint2(); var end = (SketchPoint)arc.GetEndPoint2();
                    double cx = center.X * 1000.0, cy = center.Y * 1000.0, radius = arc.GetRadius() * 1000.0;
                    double sx = start.X * 1000.0, sy = start.Y * 1000.0, ex = end.X * 1000.0, ey = end.Y * 1000.0;
                    int rotation = arc.GetRotationDir();
                    Include(points, sx, sy); Include(points, ex, ey);
                    if (arc.IsCircle() != 0)
                    {
                        Include(points, cx - radius, cy - radius); Include(points, cx + radius, cy + radius);
                    }
                    else
                    {
                        foreach (double angle in new[] { 0.0, Math.PI / 2, Math.PI, 3 * Math.PI / 2 })
                            if (AngleOnArc(angle, Math.Atan2(sy - cy, sx - cx), Math.Atan2(ey - cy, ex - cx), rotation))
                                Include(points, cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
                    }
                    nativeSegments.Add(new JObject { ["type"] = "Arc", ["centerXmm"] = cx, ["centerYmm"] = cy,
                        ["radiusMm"] = radius, ["startXmm"] = sx, ["startYmm"] = sy,
                        ["endXmm"] = ex, ["endYmm"] = ey, ["rotationDirection"] = rotation,
                        ["isCircle"] = arc.IsCircle() != 0 });
                    arcs++;
                }
                else throw new InvalidOperationException("The native profile contains an unsupported segment type.");
            }
            if (segments.Length == 0 || points.Count == 0)
                throw new InvalidOperationException("A native cut parent sketch contains no closed-profile geometry.");
            return new JObject
            {
                ["parentRelationVerified"] = true, ["sketchName"] = sketchName,
                ["segmentCount"] = segments.Length, ["lineCount"] = lines, ["arcCount"] = arcs,
                ["minXmm"] = points.Min(point => point.Item1), ["maxXmm"] = points.Max(point => point.Item1),
                ["minYmm"] = points.Min(point => point.Item2), ["maxYmm"] = points.Max(point => point.Item2),
                ["segments"] = nativeSegments
            };
        }

        private static bool AngleOnArc(double angle, double start, double end, int rotation)
        {
            double Normalize(double value)
            {
                value %= 2 * Math.PI;
                return value < 0 ? value + 2 * Math.PI : value;
            }
            double sweep = rotation < 0 ? Normalize(start - end) : Normalize(end - start);
            double distance = rotation < 0 ? Normalize(start - angle) : Normalize(angle - start);
            return distance <= sweep + 1e-10;
        }

        private static void Include(List<Tuple<double, double>> points, double x, double y)
        {
            if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y))
                throw new InvalidOperationException("A native sketch segment contains non-finite coordinates.");
            points.Add(Tuple.Create(x, y));
        }

        public static RebuildInspectionResult RebuildAndInspect(ModelDoc2 model)
        {
            if (model == null)
            {
                throw new ArgumentNullException(nameof(model));
            }

            var rebuilt = model.ForceRebuild3(false);
            var features = GetFeatureTree(model);
            var hasErrors = !rebuilt;
            var hasWarnings = false;

            foreach (var feature in features)
            {
                if (feature.ErrorCode == 0)
                {
                    continue;
                }

                if (feature.IsWarning)
                {
                    hasWarnings = true;
                }
                else
                {
                    hasErrors = true;
                }
            }

            return new RebuildInspectionResult
            {
                Rebuilt = rebuilt,
                HasErrors = hasErrors,
                HasWarnings = hasWarnings,
                Features = features
            };
        }
    }
#endif
}
