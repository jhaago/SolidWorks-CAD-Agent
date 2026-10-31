using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>Analytical area and bounds for one ordered, simple, closed line/circular-arc contour.</summary>
    public sealed class CadV2ClosedContourGeometry
    {
        private const double JoinToleranceMm = 0.000001;
        private const double GeometryToleranceMm = 0.000001;
        private const double TwoPi = 2 * Math.PI;
        private CadV2ClosedContourGeometry() { }

        public double AreaMm2 { get; private set; }
        public double MinXmm { get; private set; }
        public double MaxXmm { get; private set; }
        public double MinYmm { get; private set; }
        public double MaxYmm { get; private set; }

        public static CadV2ClosedContourGeometry Analyze(IReadOnlyList<CadCommandEnvelope> commands)
        {
            if (commands == null || commands.Count < 2)
                throw new ArgumentException("A closed contour requires at least two line or arc segments.", nameof(commands));
            var segments = commands.Select(Parse).ToArray();
            for (int i = 0; i < segments.Length; i++)
            {
                var next = segments[(i + 1) % segments.Length];
                if (Distance(segments[i].End, next.Start) > JoinToleranceMm)
                    throw new ArgumentException("Contour segments must be ordered and meet end-to-start within 0.000001 mm.", nameof(commands));
            }
            EnsureSimple(segments);

            double integral = 0;
            var result = new CadV2ClosedContourGeometry
            {
                MinXmm = double.PositiveInfinity, MaxXmm = double.NegativeInfinity,
                MinYmm = double.PositiveInfinity, MaxYmm = double.NegativeInfinity
            };
            foreach (var segment in segments)
            {
                integral += segment.AreaIntegral;
                result.Include(segment.Start);
                result.Include(segment.End);
                if (!segment.IsArc) continue;
                foreach (var angle in new[] { 0.0, Math.PI / 2, Math.PI, Math.PI * 1.5 })
                    if (segment.ContainsAngle(angle))
                        result.Include(new Point(segment.Cx + segment.Radius * Math.Cos(angle), segment.Cy + segment.Radius * Math.Sin(angle)));
            }
            result.AreaMm2 = Math.Abs(integral) / 2.0;
            if (!Finite(result.AreaMm2) || result.AreaMm2 <= 1e-12 ||
                !Finite(result.MinXmm) || !Finite(result.MaxXmm) || !Finite(result.MinYmm) || !Finite(result.MaxYmm) ||
                result.MaxXmm - result.MinXmm <= GeometryToleranceMm || result.MaxYmm - result.MinYmm <= GeometryToleranceMm ||
                !Finite((result.MaxXmm - result.MinXmm) / 1000.0) || !Finite((result.MaxYmm - result.MinYmm) / 1000.0))
                throw new ArgumentException("The closed contour must enclose finite, nondegenerate area and bounds.", nameof(commands));
            return result;
        }

        private void Include(Point point)
        {
            MinXmm = Math.Min(MinXmm, point.X); MaxXmm = Math.Max(MaxXmm, point.X);
            MinYmm = Math.Min(MinYmm, point.Y); MaxYmm = Math.Max(MaxYmm, point.Y);
        }

        private static Segment Parse(CadCommandEnvelope command)
        {
            if (command == null || command.Parameters == null)
                throw new ArgumentException("Contour segments require command parameters.", nameof(command));
            var p = command.Parameters;
            if (command.Command == CadCommandNames.AddLine)
            {
                var start = PointFrom(p, "startXmm", "startYmm");
                var end = PointFrom(p, "endXmm", "endYmm");
                if (Distance(start, end) <= GeometryToleranceMm)
                    throw new ArgumentException("Contour lines must have distinct endpoints.", nameof(command));
                return Segment.Line(start, end);
            }
            if (command.Command != CadCommandNames.AddArc)
                throw new ArgumentException("A contour may contain only AddLine and AddArc commands.", nameof(command));
            var center = PointFrom(p, "centerXmm", "centerYmm");
            var arcStart = PointFrom(p, "startXmm", "startYmm");
            var arcEnd = PointFrom(p, "endXmm", "endYmm");
            double radius = Distance(center, arcStart), endRadius = Distance(center, arcEnd);
            if (p["clockwise"]?.Type != JTokenType.Boolean || radius <= GeometryToleranceMm ||
                Math.Abs(radius - endRadius) > Math.Max(GeometryToleranceMm, radius * 1e-8))
                throw new ArgumentException("Contour arcs require an explicit direction and equal, nonzero finite endpoint radii.", nameof(command));
            bool clockwise = p.Value<bool>("clockwise");
            double a0 = Math.Atan2(arcStart.Y - center.Y, arcStart.X - center.X);
            double a1 = Math.Atan2(arcEnd.Y - center.Y, arcEnd.X - center.X);
            double ccw = PositiveAngle(a1 - a0);
            double sweep = clockwise ? ccw - TwoPi : ccw;
            double integral = center.X * (arcEnd.Y - arcStart.Y) - center.Y * (arcEnd.X - arcStart.X) + radius * radius * sweep;
            return Segment.Arc(arcStart, arcEnd, center.X, center.Y, radius, a0, sweep, integral);
        }

        private static Point PointFrom(JObject p, string xName, string yName)
        {
            if (p[xName]?.Type != JTokenType.Integer && p[xName]?.Type != JTokenType.Float ||
                p[yName]?.Type != JTokenType.Integer && p[yName]?.Type != JTokenType.Float)
                throw new ArgumentException("Contour coordinates must be numeric.");
            double x = p.Value<double>(xName), y = p.Value<double>(yName);
            if (!Finite(x) || !Finite(y)) throw new ArgumentException("Contour coordinates must be finite.");
            return new Point(x, y);
        }

        private static void EnsureSimple(Segment[] segments)
        {
            for (int i = 0; i < segments.Length; i++)
                for (int j = i + 1; j < segments.Length; j++)
                {
                    var intersections = Intersections(segments[i], segments[j]);
                    bool adjacent = j == i + 1 || i == 0 && j == segments.Length - 1;
                    if (!adjacent && intersections.Count > 0)
                        throw new ArgumentException("The contour self-intersects or touches itself.");
                    if (!adjacent) continue;
                    var shared = j == i + 1 ? segments[i].End : segments[i].Start;
                    var secondShared = segments.Length == 2
                        ? (j == i + 1 ? segments[j].End : segments[j].Start)
                        : (Point?)null;
                    if (intersections.Any(point => Distance(point, shared) > JoinToleranceMm &&
                        (!secondShared.HasValue || Distance(point, secondShared.Value) > JoinToleranceMm)))
                        throw new ArgumentException("Adjacent contour segments intersect beyond their shared endpoint.");
                }
        }

        private static List<Point> Intersections(Segment a, Segment b)
        {
            if (!a.IsArc && !b.IsArc) return LineLine(a.Start, a.End, b.Start, b.End);
            if (!a.IsArc) return LineArc(a.Start, a.End, b);
            if (!b.IsArc) return LineArc(b.Start, b.End, a);
            return ArcArc(a, b);
        }

        private static List<Point> LineLine(Point p, Point p2, Point q, Point q2)
        {
            double rx = p2.X - p.X, ry = p2.Y - p.Y, sx = q2.X - q.X, sy = q2.Y - q.Y;
            double denominator = Cross(rx, ry, sx, sy), qpx = q.X - p.X, qpy = q.Y - p.Y;
            double parallelTolerance = GeometryToleranceMm * Math.Sqrt((rx * rx + ry * ry) * (sx * sx + sy * sy));
            if (Math.Abs(denominator) <= parallelTolerance)
            {
                double collinearTolerance = GeometryToleranceMm * Math.Sqrt(rx * rx + ry * ry);
                if (Math.Abs(Cross(qpx, qpy, rx, ry)) > collinearTolerance) return new List<Point>();
                double length2 = rx * rx + ry * ry;
                double t0 = (qpx * rx + qpy * ry) / length2;
                double t1 = t0 + (sx * rx + sy * ry) / length2;
                double low = Math.Max(0, Math.Min(t0, t1)), high = Math.Min(1, Math.Max(t0, t1));
                if (low > high + GeometryToleranceMm) return new List<Point>();
                var overlaps = new List<Point> { new Point(p.X + Math.Max(0, Math.Min(1, low)) * rx, p.Y + Math.Max(0, Math.Min(1, low)) * ry) };
                if (high - low > GeometryToleranceMm)
                    overlaps.Add(new Point(p.X + Math.Max(0, Math.Min(1, high)) * rx, p.Y + Math.Max(0, Math.Min(1, high)) * ry));
                return overlaps;
            }
            double t = Cross(qpx, qpy, sx, sy) / denominator;
            double u = Cross(qpx, qpy, rx, ry) / denominator;
            return t >= -GeometryToleranceMm && t <= 1 + GeometryToleranceMm && u >= -GeometryToleranceMm && u <= 1 + GeometryToleranceMm
                ? new List<Point> { new Point(p.X + t * rx, p.Y + t * ry) } : new List<Point>();
        }

        private static List<Point> LineArc(Point start, Point end, Segment arc)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double fx = start.X - arc.Cx, fy = start.Y - arc.Cy;
            double aa = dx * dx + dy * dy, bb = 2 * (fx * dx + fy * dy), cc = fx * fx + fy * fy - arc.Radius * arc.Radius;
            double discriminant = bb * bb - 4 * aa * cc;
            if (discriminant < -GeometryToleranceMm) return new List<Point>();
            double root = Math.Sqrt(Math.Max(0, discriminant));
            var points = new List<Point>();
            AddLineArcRoot(points, start, dx, dy, (-bb - root) / (2 * aa), arc);
            if (root > GeometryToleranceMm) AddLineArcRoot(points, start, dx, dy, (-bb + root) / (2 * aa), arc);
            return points;
        }

        private static void AddLineArcRoot(List<Point> points, Point start, double dx, double dy, double t, Segment arc)
        {
            if (t < -GeometryToleranceMm || t > 1 + GeometryToleranceMm) return;
            var point = new Point(start.X + t * dx, start.Y + t * dy);
            if (arc.ContainsAngle(Math.Atan2(point.Y - arc.Cy, point.X - arc.Cx))) points.Add(point);
        }

        private static List<Point> ArcArc(Segment a, Segment b)
        {
            double dx = b.Cx - a.Cx, dy = b.Cy - a.Cy, distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= GeometryToleranceMm)
            {
                if (Math.Abs(a.Radius - b.Radius) > GeometryToleranceMm) return new List<Point>();
                var coincident = new List<Point>();
                foreach (var p in new[] { a.Start, a.End, b.Start, b.End })
                    if (a.ContainsAngle(Math.Atan2(p.Y - a.Cy, p.X - a.Cx)) && b.ContainsAngle(Math.Atan2(p.Y - b.Cy, p.X - b.Cx)) &&
                        !coincident.Any(existing => Distance(existing, p) <= JoinToleranceMm)) coincident.Add(p);
                double mid = a.StartAngle + a.Sweep / 2;
                if (b.ContainsAngle(Math.Atan2(a.Cy + a.Radius * Math.Sin(mid) - b.Cy,
                    a.Cx + a.Radius * Math.Cos(mid) - b.Cx))) coincident.Add(new Point(a.Cx + a.Radius * Math.Cos(mid), a.Cy + a.Radius * Math.Sin(mid)));
                return coincident;
            }
            if (distance > a.Radius + b.Radius + GeometryToleranceMm || distance < Math.Abs(a.Radius - b.Radius) - GeometryToleranceMm)
                return new List<Point>();
            double along = (a.Radius * a.Radius - b.Radius * b.Radius + distance * distance) / (2 * distance);
            double height2 = a.Radius * a.Radius - along * along;
            if (height2 < -GeometryToleranceMm) return new List<Point>();
            double height = Math.Sqrt(Math.Max(0, height2)), baseX = a.Cx + along * dx / distance, baseY = a.Cy + along * dy / distance;
            var candidates = new[]
            {
                new Point(baseX - height * dy / distance, baseY + height * dx / distance),
                new Point(baseX + height * dy / distance, baseY - height * dx / distance)
            };
            return candidates.Where(p => a.ContainsAngle(Math.Atan2(p.Y - a.Cy, p.X - a.Cx)) &&
                b.ContainsAngle(Math.Atan2(p.Y - b.Cy, p.X - b.Cx))).Distinct(new PointComparer()).ToList();
        }

        private static double PositiveAngle(double angle)
        {
            angle %= TwoPi;
            return angle < 0 ? angle + TwoPi : angle;
        }
        private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
        private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private struct Point
        {
            internal Point(double x, double y) { X = x; Y = y; }
            internal double X, Y;
        }

        private sealed class PointComparer : IEqualityComparer<Point>
        {
            public bool Equals(Point x, Point y) => Distance(x, y) <= GeometryToleranceMm;
            public int GetHashCode(Point obj) => 0;
        }

        private sealed class Segment
        {
            private Segment() { }
            internal Point Start, End;
            internal bool IsArc;
            internal double Cx, Cy, Radius, StartAngle, Sweep, AreaIntegral;
            internal static Segment Line(Point start, Point end) => new Segment
            { Start = start, End = end, AreaIntegral = Cross(start.X, start.Y, end.X, end.Y) };
            internal static Segment Arc(Point start, Point end, double cx, double cy, double radius,
                double startAngle, double sweep, double integral) => new Segment
                { Start = start, End = end, IsArc = true, Cx = cx, Cy = cy, Radius = radius,
                    StartAngle = startAngle, Sweep = sweep, AreaIntegral = integral };
            internal bool ContainsAngle(double angle)
            {
                double delta = Sweep >= 0 ? PositiveAngle(angle - StartAngle) : PositiveAngle(StartAngle - angle);
                return delta <= Math.Abs(Sweep) + GeometryToleranceMm / Math.Max(Radius, GeometryToleranceMm);
            }
        }
    }
}
