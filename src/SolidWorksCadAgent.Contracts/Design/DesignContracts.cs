using System;
using System.Collections.Generic;

namespace SolidWorksCadAgent.Contracts.Design
{
    public sealed class DesignReference
    {
        public Guid Id { get; set; }
        public string FileName { get; set; }
        public string RelativePath { get; set; }
        public string MediaType { get; set; }
        public string Label { get; set; }
        public string ViewType { get; set; }
        public DateTime AddedUtc { get; set; }
        public string Sha256 { get; set; }
        public int ByteLength { get; set; }
    }
    public sealed class DesignQuestion
    {
        public string Id { get; set; }
        public string Question { get; set; }
        public bool Critical { get; set; }
        public string Answer { get; set; }
    }
    public sealed class DesignInterpretation
    {
        public string Summary { get; set; }
        public List<string> Observations { get; set; } = new List<string>();
        public List<string> VisibleText { get; set; } = new List<string>();
        public List<string> VisibleDimensions { get; set; } = new List<string>();
        public List<string> Inferences { get; set; } = new List<string>();
        public List<string> Assumptions { get; set; } = new List<string>();
        public List<string> Unknowns { get; set; } = new List<string>();
        public List<DesignUnknownResolution> ResolvedUnknowns { get; set; } = new List<DesignUnknownResolution>();
        public List<string> MissingDimensions { get; set; } = new List<string>();
        public List<string> Dimensions { get; set; } = new List<string>();
        public List<string> Constraints { get; set; } = new List<string>();
        public List<string> FeatureIntent { get; set; } = new List<string>();
        public List<string> Materials { get; set; } = new List<string>();
        public List<string> SuggestedViews { get; set; } = new List<string>();
        public string ModellingStrategy { get; set; }
        public List<string> RequiredCadFeatures { get; set; } = new List<string>();
        public List<string> UnsupportedFeatures { get; set; } = new List<string>();
        public List<DesignQuestion> Questions { get; set; } = new List<DesignQuestion>();
        public string Confidence { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }
    public sealed class DesignUnknownResolution
    {
        public string Unknown { get; set; }
        public string Evidence { get; set; }
    }
    public sealed class DesignMessage
    {
        public string Role { get; set; }
        public string Text { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
    public sealed class DesignBriefRevision
    {
        public Guid Id { get; set; }
        public int Number { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<Guid> ReferenceIds { get; set; } = new List<Guid>();
        public DesignInterpretation Brief { get; set; }
    }
    public sealed class DesignSession
    {
        public Guid Id { get; set; }
        public string Title { get; set; }
        public string State { get; set; } = "ImageReceived";
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<DesignReference> References { get; set; } = new List<DesignReference>();
        public List<DesignMessage> Messages { get; set; } = new List<DesignMessage>();
        public List<DesignBriefRevision> Revisions { get; set; } = new List<DesignBriefRevision>();
        public Guid? ApprovedRevisionId { get; set; }
        public Guid? CadJobId { get; set; }
        public string LastError { get; set; }
    }
}
