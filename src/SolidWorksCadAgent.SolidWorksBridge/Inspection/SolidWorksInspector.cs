using System;
using System.Collections.Generic;
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

            return part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? Array.Empty<object>();
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

            var result = new List<FeatureInspectionItem>();
            Feature feature = model.IFirstFeature();
            while (feature != null)
            {
                bool isWarning;
                var errorCode = feature.GetErrorCode2(out isWarning);
                result.Add(new FeatureInspectionItem
                {
                    Name = feature.Name,
                    TypeName = feature.GetTypeName2(),
                    ErrorCode = errorCode,
                    IsWarning = errorCode != 0 && isWarning
                });

                feature = feature.GetNextFeature() as Feature;
            }

            return result;
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
