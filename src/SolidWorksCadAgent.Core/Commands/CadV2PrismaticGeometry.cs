using System;
using System.Collections.Generic;
using System.Linq;
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
        public IReadOnlyList<CadV2CutExpectation> Cuts { get; private set; } = new List<CadV2CutExpectation>();
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
                    if (CadParametricHoleContract.IsHole(plan))
                    {
                        // Whole-plan preflight above has validated the exact dimensioned-hole scope.
                        var margin=CadParametricHoleContract.MinimumEdgeClearanceMm;
                        Require(profile.MinX >= boss.MinX + margin && profile.MaxX <= boss.MaxX - margin &&
                            profile.MinY >= boss.MinY + margin && profile.MaxY <= boss.MaxY - margin,
                            "Dimensioned hole bounds must retain at least 0.001 mm clearance.");
                    }
                    else
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
                    result._cutExpectations.Add(new CadV2CutExpectation(feature.StepKey,
                        feature.Parameters.Value<string>("endCondition"),
                        feature.Parameters.Value<string>("endCondition") == "Blind" ? (double?)cutDepth : null,
                        profile.MinX, profile.MaxX, profile.MinY, profile.MaxY, profile.LineCount, profile.ArcCount,
                        profile.Segments.AsReadOnly()));
                }
                Require(Positive(result.VolumeMm3), "The computed solid volume must be finite and positive.");
                result.LastFeatureStepKey = feature.StepKey;
            }
            Require(group > 0 && index == end, "The plan contains an incomplete prismatic feature group.");
            result.Cuts = result._cutExpectations.AsReadOnly();
            return result;
        }

        private readonly List<CadV2CutExpectation> _cutExpectations = new List<CadV2CutExpectation>();

        private sealed class Profile
        {
            internal double Area, MinX, MaxX, MinY, MaxY;
            internal bool IsRectangle;
            internal int LineCount, ArcCount;
            internal List<CadV2ProfileSegmentExpectation> Segments = new List<CadV2ProfileSegmentExpectation>();
        }

        private static bool IsProfileCommand(string command) => command == CadCommandNames.AddRectangle ||
            command == CadCommandNames.AddCircle || command == CadCommandNames.AddSlot ||
            command == CadCommandNames.AddRegularPolygon || command == CadCommandNames.AddLine || command == CadCommandNames.AddArc;

        private static Profile ReadProfile(IReadOnlyList<CadPlanV2Step> primitives)
        {
            Profile profile;
            if (primitives.Count == 1 && primitives[0].Command != CadCommandNames.AddLine && primitives[0].Command != CadCommandNames.AddArc)
                profile = ReadPrimitive(primitives[0]);
            else
            {
                var contourCommands = new List<CadCommandEnvelope>();
                foreach (var step in primitives)
                {
                    if (step.Command != CadCommandNames.AddLine && step.Command != CadCommandNames.AddArc)
                        throw new ArgumentException("A line/arc contour cannot be mixed with standalone profile primitives.", "plan");
                    contourCommands.Add(new CadCommandEnvelope { Command = step.Command, Parameters = step.Parameters });
                }
                var contour = CadV2ClosedContourGeometry.Analyze(contourCommands);
                profile = new Profile
                {
                    Area = contour.AreaMm2, MinX = contour.MinXmm, MaxX = contour.MaxXmm,
                    MinY = contour.MinYmm, MaxY = contour.MaxYmm,
                    LineCount = primitives.Count(step => step.Command == CadCommandNames.AddLine),
                    ArcCount = primitives.Count(step => step.Command == CadCommandNames.AddArc)
                };
            }
            profile.Segments = ReadProfileSegments(primitives);
            return profile;
        }

        private static List<CadV2ProfileSegmentExpectation> ReadProfileSegments(IReadOnlyList<CadPlanV2Step> primitives)
        {
            if (primitives.Count == 1)
            {
                var primitive = primitives[0];
                var p = primitive.Parameters;
                double x = Number(p, "centerXmm"), y = Number(p, "centerYmm");
                if (primitive.Command == CadCommandNames.AddRectangle)
                {
                    double halfWidth = Number(p, "widthMm") / 2, halfHeight = Number(p, "heightMm") / 2;
                    double minX = x - halfWidth, maxX = x + halfWidth, minY = y - halfHeight, maxY = y + halfHeight;
                    return new List<CadV2ProfileSegmentExpectation>
                    {
                        Line(minX, minY, maxX, minY), Line(maxX, minY, maxX, maxY),
                        Line(maxX, maxY, minX, maxY), Line(minX, maxY, minX, minY)
                    };
                }
                if (primitive.Command == CadCommandNames.AddCircle)
                {
                    double radius = Number(p, "diameterMm") / 2;
                    return new List<CadV2ProfileSegmentExpectation> { Circle(x, y, radius) };
                }
                var generated = primitive.Command == CadCommandNames.AddSlot
                    ? PrismaticProfileGeometry.Slot(p)
                    : PrismaticProfileGeometry.RegularPolygon(p);
                return generated.Select(ReadProfileSegment).ToList();
            }
            return primitives.Select(step => ReadProfileSegment(new CadCommandEnvelope
            { Command = step.Command, Parameters = step.Parameters })).ToList();
        }

        private static CadV2ProfileSegmentExpectation ReadProfileSegment(CadCommandEnvelope command)
        {
            var p = command.Parameters;
            if (command.Command == CadCommandNames.AddLine)
                return Line(Number(p, "startXmm"), Number(p, "startYmm"), Number(p, "endXmm"), Number(p, "endYmm"));
            if (command.Command == CadCommandNames.AddArc)
            {
                double centerX = Number(p, "centerXmm"), centerY = Number(p, "centerYmm");
                double startX = Number(p, "startXmm"), startY = Number(p, "startYmm");
                double radius = Math.Sqrt((startX - centerX) * (startX - centerX) + (startY - centerY) * (startY - centerY));
                return new CadV2ProfileSegmentExpectation("Arc", startX, startY, Number(p, "endXmm"), Number(p, "endYmm"),
                    centerX, centerY, radius, p.Value<bool>("clockwise"), false);
            }
            throw new ArgumentException("A cut profile contains an unsupported native segment.", "plan");
        }

        private static CadV2ProfileSegmentExpectation Line(double startX, double startY, double endX, double endY) =>
            new CadV2ProfileSegmentExpectation("Line", startX, startY, endX, endY, 0, 0, 0, false, false);

        private static CadV2ProfileSegmentExpectation Circle(double centerX, double centerY, double radius) =>
            new CadV2ProfileSegmentExpectation("Arc", centerX + radius, centerY, centerX + radius, centerY,
                centerX, centerY, radius, false, true);

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
                    profile.LineCount = 4;
                    break;
                case CadCommandNames.AddCircle:
                    double radius = Number(p, "diameterMm") / 2;
                    halfX = halfY = radius;
                    profile.Area = Math.PI * radius * radius;
                    profile.ArcCount = 1;
                    break;
                case CadCommandNames.AddSlot:
                    double slotWidth = Number(p, "widthMm"), straight = Number(p, "lengthMm") - slotWidth;
                    double slotRadius = slotWidth / 2, angle = Number(p, "angleDegrees") * Math.PI / 180;
                    // A capsule is a line segment expanded by its radius in every direction.
                    halfX = straight / 2 * Math.Abs(Math.Cos(angle)) + slotRadius;
                    halfY = straight / 2 * Math.Abs(Math.Sin(angle)) + slotRadius;
                    profile.Area = straight * slotWidth + Math.PI * slotRadius * slotRadius;
                    profile.LineCount = 2;
                    profile.ArcCount = 2;
                    break;
                default:
                    int sides = p.Value<int>("sides");
                    double polygonRadius = Number(p, "diameterMm") / 2;
                    double startAngle = Number(p, "angleDegrees") * Math.PI / 180;
                    profile.Area = sides * polygonRadius * polygonRadius * Math.Sin(2 * Math.PI / sides) / 2;
                    profile.LineCount = sides;
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

    /// <summary>Plan-derived native cut and parent-profile expectations used by independent readback.</summary>
    public sealed class CadV2CutExpectation
    {
        internal CadV2CutExpectation(string stepKey, string endCondition, double? depthMm,
            double minXmm, double maxXmm, double minYmm, double maxYmm, int lineCount, int arcCount,
            IReadOnlyList<CadV2ProfileSegmentExpectation> segments)
        {
            StepKey = stepKey; EndCondition = endCondition; DepthMm = depthMm;
            MinXmm = minXmm; MaxXmm = maxXmm; MinYmm = minYmm; MaxYmm = maxYmm;
            LineCount = lineCount; ArcCount = arcCount; Segments = segments;
        }
        public string StepKey { get; }
        public string EndCondition { get; }
        public double? DepthMm { get; }
        public double MinXmm { get; }
        public double MaxXmm { get; }
        public double MinYmm { get; }
        public double MaxYmm { get; }
        public int LineCount { get; }
        public int ArcCount { get; }
        public IReadOnlyList<CadV2ProfileSegmentExpectation> Segments { get; }
    }

    /// <summary>Plan-derived local geometry for one expected native sketch segment.</summary>
    public sealed class CadV2ProfileSegmentExpectation
    {
        internal CadV2ProfileSegmentExpectation(string type, double startXmm, double startYmm, double endXmm,
            double endYmm, double centerXmm, double centerYmm, double radiusMm, bool clockwise, bool isCircle)
        {
            Type = type; StartXmm = startXmm; StartYmm = startYmm; EndXmm = endXmm; EndYmm = endYmm;
            CenterXmm = centerXmm; CenterYmm = centerYmm; RadiusMm = radiusMm;
            Clockwise = clockwise; IsCircle = isCircle;
        }
        public string Type { get; }
        public double StartXmm { get; }
        public double StartYmm { get; }
        public double EndXmm { get; }
        public double EndYmm { get; }
        public double CenterXmm { get; }
        public double CenterYmm { get; }
        public double RadiusMm { get; }
        public bool Clockwise { get; }
        public bool IsCircle { get; }
    }
}
