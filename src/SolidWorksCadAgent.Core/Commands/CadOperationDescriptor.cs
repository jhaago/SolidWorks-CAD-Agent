using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>
    /// Describes the currently supported legacy command contract for planner metadata.
    /// The incoming CadCommandEnvelope has no version field; all commands remain at
    /// operation version 1 until a separately reviewed transport/persistence migration.
    /// </summary>
    public sealed class CadOperationDescriptor
    {
        internal CadOperationDescriptor(string name, int operationVersion, string protocolText)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An operation name is required.", nameof(name));
            if (operationVersion < 1) throw new ArgumentOutOfRangeException(nameof(operationVersion));
            if (string.IsNullOrWhiteSpace(protocolText)) throw new ArgumentException("Protocol text is required.", nameof(protocolText));

            Name = name;
            OperationVersion = operationVersion;
            ProtocolText = protocolText;
        }

        public string Name { get; }
        public int OperationVersion { get; }
        public string ProtocolText { get; }
    }

    /// <summary>
    /// The planner's single source for supported operation names and their prompt contract.
    /// Descriptor versions document current semantics; they are not serialized into legacy plans.
    /// </summary>
    public static class CadOperationCatalog
    {
        private static readonly ReadOnlyCollection<CadOperationDescriptor> DescriptorList = Array.AsReadOnly(new[]
        {
            Descriptor(CadCommandNames.NewPart, "NewPart {}"),
            Descriptor(CadCommandNames.OpenPart, "OpenPart {path:string}"),
            Descriptor(CadCommandNames.SavePart, "SavePart {path:string, allowOverwrite:false}; overwrite permission is server-controlled and the model must never set it true."),
            Descriptor(CadCommandNames.CloseDocument, "CloseDocument {}"),
            Descriptor(CadCommandNames.CreateSketch,
                "CreateSketch {plane:'Top Plane'|'Front Plane'|'Right Plane'}\n" +
                "Standard origin-plane positive bosses: Top Plane +Y, Front Plane +Z, Right Plane +X in the installed SOLIDWORKS 2020 template."),
            Descriptor(CadCommandNames.AddLine, "AddLine {startXmm:number, startYmm:number, endXmm:number, endYmm:number}; endpoints must be distinct."),
            Descriptor(CadCommandNames.AddArc, "AddArc {centerXmm:number, centerYmm:number, startXmm:number, startYmm:number, endXmm:number, endYmm:number, clockwise:boolean}; endpoints must be distinct and have equal nonzero radii."),
            Descriptor(CadCommandNames.AddRectangle, "AddRectangle {centerXmm:number, centerYmm:number, widthMm:number>0, heightMm:number>0}"),
            Descriptor(CadCommandNames.AddCircle, "AddCircle {centerXmm:number, centerYmm:number, diameterMm:number>0}"),
            Descriptor(CadCommandNames.AddSlot, "AddSlot {centerXmm:number, centerYmm:number, lengthMm:number, widthMm:number, angleDegrees:number}; total end-to-end lengthMm > widthMm > 0, axis angle counterclockwise in degrees [-360,360]."),
            Descriptor(CadCommandNames.AddRegularPolygon, "AddRegularPolygon {centerXmm:number, centerYmm:number, sides:integer[3,32], diameterMm:number>0, angleDegrees:number}; diameter is the circumcircle diameter, angle of first vertex counterclockwise in degrees [-360,360]."),
            Descriptor(CadCommandNames.ExitSketch, "ExitSketch {}"),
            Descriptor(CadCommandNames.Extrude, "Extrude {depthMm:number>0}"),
            Descriptor(CadCommandNames.CutExtrude, "CutExtrude {endCondition:'ThroughAll'} or {endCondition:'Blind', depthMm:number>0}; Blind depth is required, ThroughAll rejects depthMm. The implementation uses the fixed legacy direction for origin-plane sketches, historically verified for underside pockets; face/offset-plane pockets and direction overrides are unsupported."),
            Descriptor(CadCommandNames.Rebuild, "Rebuild {}")
        });

        private static readonly ReadOnlyCollection<string> NameList = Array.AsReadOnly(DescriptorList.Select(descriptor => descriptor.Name).ToArray());
        private static readonly string Description =
            "Supported CAD command protocol (parameters_json must be a JSON object using these exact names):\n" +
            string.Join("\n", DescriptorList.Select(descriptor => descriptor.ProtocolText));

        public static IReadOnlyList<CadOperationDescriptor> Descriptors => DescriptorList;
        public static IReadOnlyList<string> Names => NameList;
        public static string ProtocolDescription => Description;

        public static CadOperationDescriptor Find(string name) =>
            name == null ? null : DescriptorList.FirstOrDefault(descriptor => string.Equals(descriptor.Name, name, StringComparison.Ordinal));

        private static CadOperationDescriptor Descriptor(string name, string protocolText) =>
            new CadOperationDescriptor(name, 1, protocolText);
    }
}
