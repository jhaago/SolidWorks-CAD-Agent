using System;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>A finite length expressed in millimetres at the legacy command boundary.</summary>
    public readonly struct LengthMm
    {
        public LengthMm(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(nameof(value), "A CAD length must be finite.");
            Value = value;
        }

        public double Value { get; }
    }

    /// <summary>A 2D point in the local millimetre coordinate frame of its sketch.</summary>
    public readonly struct Point2Mm
    {
        public Point2Mm(LengthMm x, LengthMm y)
        {
            X = x;
            Y = y;
        }

        public LengthMm X { get; }
        public LengthMm Y { get; }
    }

    public enum SketchPlane
    {
        Top,
        Front,
        Right
    }

    /// <summary>An immutable typed operation lowered from a supported legacy envelope.</summary>
    public abstract class CadOperation
    {
        protected CadOperation(CadOperationDescriptor descriptor)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        }

        public CadOperationDescriptor Descriptor { get; }
        public string Name => Descriptor.Name;
        public int OperationVersion => Descriptor.OperationVersion;
        public abstract CadCommandEnvelope ToCommandEnvelope();
    }

    public sealed class NewPartOperation : CadOperation
    {
        internal NewPartOperation(CadOperationDescriptor descriptor) : base(descriptor) { }

        public override CadCommandEnvelope ToCommandEnvelope() =>
            new CadCommandEnvelope { Command = Name, Parameters = new JObject() };
    }

    public sealed class CreateSketchOperation : CadOperation
    {
        internal CreateSketchOperation(CadOperationDescriptor descriptor, SketchPlane plane) : base(descriptor)
        {
            Plane = plane;
        }

        public SketchPlane Plane { get; }

        public override CadCommandEnvelope ToCommandEnvelope() => new CadCommandEnvelope
        {
            Command = Name,
            Parameters = new JObject { ["plane"] = PlaneName(Plane) }
        };

        private static string PlaneName(SketchPlane plane)
        {
            switch (plane)
            {
                case SketchPlane.Top: return "Top Plane";
                case SketchPlane.Front: return "Front Plane";
                case SketchPlane.Right: return "Right Plane";
                default: throw new ArgumentOutOfRangeException(nameof(plane));
            }
        }
    }

    public sealed class AddLineOperation : CadOperation
    {
        internal AddLineOperation(CadOperationDescriptor descriptor, Point2Mm start, Point2Mm end) : base(descriptor)
        {
            Start = start;
            End = end;
        }

        public Point2Mm Start { get; }
        public Point2Mm End { get; }

        public override CadCommandEnvelope ToCommandEnvelope() => new CadCommandEnvelope
        {
            Command = Name,
            Parameters = new JObject
            {
                ["startXmm"] = Start.X.Value,
                ["startYmm"] = Start.Y.Value,
                ["endXmm"] = End.X.Value,
                ["endYmm"] = End.Y.Value
            }
        };
    }

    public sealed class AddCircleOperation : CadOperation
    {
        internal AddCircleOperation(CadOperationDescriptor descriptor, Point2Mm center, LengthMm diameter) : base(descriptor)
        {
            Center = center;
            Diameter = diameter;
        }

        public Point2Mm Center { get; }
        public LengthMm Diameter { get; }

        public override CadCommandEnvelope ToCommandEnvelope() => new CadCommandEnvelope
        {
            Command = Name,
            Parameters = new JObject
            {
                ["centerXmm"] = Center.X.Value,
                ["centerYmm"] = Center.Y.Value,
                ["diameterMm"] = Diameter.Value
            }
        };
    }
}
