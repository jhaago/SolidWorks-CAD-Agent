using System;
using System.IO;

namespace SolidWorksCadAgent.Core.Workspace
{
    /// <summary>Deterministic owned staging path for the first v2 non-overwriting part save.</summary>
    public static class CadV2SavePath
    {
        public static string StageFor(string finalPath, Guid modelId)
        {
            if (string.IsNullOrWhiteSpace(finalPath) || !Path.IsPathRooted(finalPath) || modelId == Guid.Empty)
                throw new ArgumentException("An absolute final path and managed model ID are required.");
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(finalPath)),
                ".cad-agent-stage-" + modelId.ToString("N") + ".sldprt");
        }

        public static string PublishFor(string finalPath, Guid modelId)
        {
            var stage = StageFor(finalPath, modelId);
            return Path.Combine(Path.GetDirectoryName(stage),
                Path.GetFileNameWithoutExtension(stage) + ".publish.sldprt");
        }
    }
}
