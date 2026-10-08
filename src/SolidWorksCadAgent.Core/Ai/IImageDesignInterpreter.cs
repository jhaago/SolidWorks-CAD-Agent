using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.Contracts.Design;

namespace SolidWorksCadAgent.Core.Ai
{
    public sealed class DesignReferenceContent
    {
        public DesignReference Reference { get; set; }
        public byte[] Bytes { get; set; }
    }
    public sealed class ImageDesignRequest
    {
        public DesignSession Design { get; set; }
        public List<DesignReferenceContent> Images { get; set; } = new List<DesignReferenceContent>();
    }
    public interface IImageDesignInterpreter
    {
        Task<DesignInterpretation> InterpretAsync(ImageDesignRequest request, CancellationToken cancellationToken);
    }
}
