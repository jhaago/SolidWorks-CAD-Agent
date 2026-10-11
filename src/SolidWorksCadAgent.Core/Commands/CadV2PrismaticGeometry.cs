using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>Analytical expectations for one origin-plane boss and disjoint interior cuts.</summary>
    public sealed class CadV2PrismaticGeometry
    {
        private const double InteriorMarginMm = 0.02;
        private CadV2PrismaticGeometry() { }
        public double VolumeMm3 { get; private set; }
        public double MinXmm { get; private set; }
        public double MaxXmm { get; private set; }
        public double MinYmm { get; private set; }
        public double MaxYmm { get; private set; }
        public double MinZmm { get; private set; }
        public double MaxZmm { get; private set; }
        public double DepthMm { get; private set; }
        public string PlaneName { get; private set; }
        public string LastFeatureStepKey { get; private set; }
        public CadPlanV2Step SaveStep { get; private set; }
        public JObject ExpectedBounds() => new JObject
        {
            ["minXmm"] = MinXmm, ["maxXmm"] = MaxXmm,
            ["minYmm"] = MinYmm, ["maxYmm"] = MaxYmm,
            ["minZmm"] = MinZmm, ["maxZmm"] = MaxZmm,
            ["sizeXmm"] = MaxXmm - MinXmm, ["sizeYmm"] = MaxYmm - MinYmm,
            ["sizeZmm"] = MaxZmm - MinZmm
        };

        public static CadV2PrismaticGeometry Analyze(CadPlanV2Document plan)
        {
            try { return AnalyzeCore(plan); }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException || ex is InvalidCastException)
            { throw new ArgumentException("The plan contains unrepresentable geometry.", nameof(plan), ex); }
        }

        private static CadV2PrismaticGeometry AnalyzeCore(CadPlanV2Document plan)
        {
            var errors = CadPlanV2Preflight.Validate(plan, true);
            Require(errors.Count == 0, errors.Count == 0 ? null : errors[0]);
            var steps = plan.Steps;
            int end = steps.Count;
            var result = new CadV2PrismaticGeometry();
            if (steps[end - 1].Command == CadCommandNames.SavePart)
            {
                result.SaveStep = steps[--end];
                Require(result.SaveStep.Parameters["allowOverwrite"]?.Type == JTokenType.Boolean &&
                    !result.SaveStep.Parameters.Value<bool>("allowOverwrite"), "SavePart must explicitly disallow overwrite.");
            }
            Require(end >= 5 && steps[0].Command == CadCommandNames.NewPart,
                "Expected NewPart, complete sketch/profile/exit/feature groups, and an optional final SavePart.");
            Profile boss = null;
            CadStandardPlaneFrame frame = null;
            var cuts = new List<Profile>();
            int index = 1;
            int group = 0;
            while (index < end)
            {
                var sketch = steps[index++];
                bool first = group++ == 0;
                Require(sketch != null && sketch.Command == CadCommandNames.CreateSketch,
                    "Each prismatic group must begin with CreateSketch.");
                var primitives = new List<CadPlanV2Step>();
                while (index < end && IsProfileCommand(steps[index]?.Command)) primitives.Add(steps[index++]);
                Require(primitives.Count > 0, "Each sketch must contain one supported profile or a complete line/arc contour.");
                Require(index < end && steps[index].Command == CadCommandNames.ExitSketch,
                    "Each sketch profile must be followed by ExitSketch.");
                index++;
                Require(index < end, "Each closed sketch must be consumed by one feature.");
                var feature = steps[index++];
                Require(sketch.Command == CadCommandNames.CreateSketch &&
                    feature.Command == (first ? CadCommandNames.Extrude : CadCommandNames.CutExtrude),
                    "Only one-profile standard-plane groups with one initial boss followed by cuts are supported.");
                CadStandardPlaneFrame groupFrame;
                try { groupFrame = CadStandardPlaneFrame.FromPlaneName(sketch.Parameters.Value<string>("plane")); }
                catch (ArgumentException) { throw new ArgumentException("Every sketch must use the Top Plane, Front Plane or Right Plane.", "plan"); }
                if (first)
                {
                    frame = groupFrame;
                    result.PlaneName = frame.PlaneName;
                }
                else
                    Require(groupFrame.PlaneName == frame.PlaneName,
                        "All profiles in a prismatic part must use the same standard plane.");
                var reference = feature.Inputs?.ProfileSketch;
                Require(reference != null && reference.Kind == "Sketch" &&
                    reference.OutputKey == sketch.OutputKey && reference.EntityId == sketch.OutputEntityId,
                    "Each feature must reference its own immediately preceding sketch.");
                var profile = ReadProfile(primitives);
                if (first)
                {
                    boss = profile;
                    result.DepthMm = Number(feature.Parameters, "depthMm");
                    Require(Positive(result.DepthMm) && Positive(result.DepthMm / 1000), "Boss depth is not representable.");
                    result.VolumeMm3 = boss.Area * result.DepthMm;
                    var modelBounds = frame.TransformBounds(boss.MinX, boss.MaxX, boss.MinY, boss.MaxY, 0, result.DepthMm);
                    result.MinXmm = modelBounds.MinXmm; result.MaxXmm = modelBounds.MaxXmm;
                    result.MinYmm = modelBounds.MinYmm; result.MaxYmm = modelBounds.MaxYmm;
                    result.MinZmm = modelBounds.MinZmm; result.MaxZmm = modelBounds.MaxZmm;
                }
                else
                {
                    Require(boss.IsRectangle, "Cuts require a rectangular base boss.");
                    Require(profile.MinX > boss.MinX + InteriorMarginMm && profile.MaxX < boss.MaxX - InteriorMarginMm &&
                        profile.MinY > boss.MinY + InteriorMarginMm && profile.MaxY < boss.MaxY - InteriorMarginMm,
                        "Cut bounds must be strictly interior to the base with more than 0.02 mm clearance.");
                    foreach (var previous in cuts)
                        Require(profile.MaxX < previous.MinX || profile.MinX > previous.MaxX ||
                            profile.MaxY < previous.MinY || profile.MinY > previous.MaxY,
                            "Cut bounding boxes must be disjoint and must not touch.");
                    double cutDepth = result.DepthMm;
                    if (feature.Parameters.Value<string>("endCondition") == "Blind")
                    {
                        cutDepth = Number(feature.Parameters, "depthMm");
                        Require(Positive(cutDepth) && Positive(cutDepth / 1000) && cutDepth < result.DepthMm,
                            "Blind cut depth must be positive and less than the boss depth.");
                    }
                    result.VolumeMm3 -= profile.Area * cutDepth;
                    cuts.Add(profile);
                }
                Require(Positive(result.VolumeMm3), "The computed solid volume must be finite and positive.");
                result.LastFeatureStepKey = feature.StepKey;
            }
            Require(group > 0 && index == end, "The plan contains an incomplete prismatic feature group.");
            return result;
        }

        private sealed class Profile
        {
            internal double Area, MinX, MaxX, MinY, MaxY;
            internal bool IsRectangle;
        }

        private static bool IsProfileCommand(string command) => command == CadCommandNames.AddRectangle ||
            command == CadCommandNames.AddCircle || command == CadCommandNames.AddSlot ||
            command == CadCommandNames.AddRegularPolygon || command == CadCommandNames.AddLine || command == CadCommandNames.AddArc;

        private static Profile ReadProfile(IReadOnlyList<CadPlanV2Step> primitives)
        {
            if (primitives.Count == 1 && primitives[0].Command != CadCommandNames.AddLine && primitives[0].Command != CadCommandNames.AddArc)
                return ReadPrimitive(primitives[0]);
            var contourCommands = new List<CadCommandEnvelope>();
            foreach (var step in primitives)
            {
                if (step.Command != CadCommandNames.AddLine && step.Command != CadCommandNames.AddArc)
                    throw new ArgumentException("A line/arc contour cannot be mixed with standalone profile primitives.", "plan");
                contourCommands.Add(new CadCommandEnvelope { Command = step.Command, Parameters = step.Parameters });
            }
            var contour = CadV2ClosedContourGeometry.Analyze(contourCommands);
            return new Profile
            {
                Area = contour.AreaMm2, MinX = contour.MinXmm, MaxX = contour.MaxXmm,
                MinY = contour.MinYmm, MaxY = contour.MaxYmm
            };
        }

        private static Profile ReadPrimitive(CadPlanV2Step primitive)
        {
            var p = primitive.Parameters;
            Require(primitive.Command == CadCommandNames.AddRectangle || primitive.Command == CadCommandNames.AddCircle ||
                primitive.Command == CadCommandNames.AddSlot || primitive.Command == CadCommandNames.AddRegularPolygon,
                "Only one rectangle, circle, slot or regular polygon is supported in each sketch.");
            double x = Number(p, "centerXmm"), y = Number(p, "centerYmm");
            var profile = new Profile();
            double halfX, halfY;
            switch (primitive.Command)
            {
                case CadCommandNames.AddRectangle:
                    double width = Number(p, "widthMm"), height = Number(p, "heightMm");
                    halfX = width / 2; halfY = height / 2;
                    profile.Area = width * height;
                    profile.IsRectangle = true;
                    break;
                case CadCommandNames.AddCircle:
                    double radius = Number(p, "diameterMm") / 2;
                    halfX = halfY = radius;
                    profile.Area = Math.PI * radius * radius;
                    break;
                case CadCommandNames.AddSlot:
                    double slotWidth = Number(p, "widthMm"), straight = Number(p, "lengthMm") - slotWidth;
                    double slotRadius = slotWidth / 2, angle = Number(p, "angleDegrees") * Math.PI / 180;
                    // A capsule is a line segment expanded by its radius in every direction.
                    halfX = straight / 2 * Math.Abs(Math.Cos(angle)) + slotRadius;
                    halfY = straight / 2 * Math.Abs(Math.Sin(angle)) + slotRadius;
                    profile.Area = straight * slotWidth + Math.PI * slotRadius * slotRadius;
                    break;
                default:
                    int sides = p.Value<int>("sides");
                    double polygonRadius = Number(p, "diameterMm") / 2;
                    double startAngle = Number(p, "angleDegrees") * Math.PI / 180;
                    profile.Area = sides * polygonRadius * polygonRadius * Math.Sin(2 * Math.PI / sides) / 2;
                    profile.MinX = profile.MinY = double.PositiveInfinity;
                    profile.MaxX = profile.MaxY = double.NegativeInfinity;
                    // Evaluate analytical vertices independently of native generated sketch segments.
                    for (int vertex = 0; vertex < sides; vertex++)
                    {
                        double theta = startAngle + 2 * Math.PI * vertex / sides;
                        double vx = x + polygonRadius * Math.Cos(theta), vy = y + polygonRadius * Math.Sin(theta);
                        profile.MinX = Math.Min(profile.MinX, vx); profile.MaxX = Math.Max(profile.MaxX, vx);
                        profile.MinY = Math.Min(profile.MinY, vy); profile.MaxY = Math.Max(profile.MaxY, vy);
                    }
                    ValidateProfile(profile);
                    return profile;
            }
            profile.MinX = x - halfX; profile.MaxX = x + halfX;
            profile.MinY = y - halfY; profile.MaxY = y + halfY;
            ValidateProfile(profile);
            return profile;
        }

        private static void ValidateProfile(Profile profile)
        {
            Require(Positive(profile.Area) && Finite(profile.MinX) && Finite(profile.MaxX) &&
                Finite(profile.MinY) && Finite(profile.MaxY) && Positive(profile.MaxX - profile.MinX) &&
                Positive(profile.MaxY - profile.MinY) &&
                Positive((profile.MaxX - profile.MinX) / 1000) && Positive((profile.MaxY - profile.MinY) / 1000),
                "The profile area and extents must be finite and nondegenerate.");
        }
        private static double Number(JObject parameters, string name) => parameters[name].Value<double>();
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool Positive(double value) => Finite(value) && value > 0;
        private static void Require(bool condition, string reason)
        {
            if (!condition) throw new ArgumentException(reason, "plan");
        }
    }
}
