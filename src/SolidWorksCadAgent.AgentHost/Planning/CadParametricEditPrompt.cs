using System;
using System.Globalization;
using System.Text.RegularExpressions;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    public sealed class CadParametricEditPromptRequest
    {
        public Guid JobId {get;set;}
        public string Parameter {get;set;}
        public double ValueMm {get;set;}
        public string OutputPath {get;set;}
    }
    public static class CadParametricEditPrompt
    {
        private static readonly Regex Syntax=new Regex(@"\AV2 parametric edit:\s*job\s+(?<id>[a-fA-F0-9-]{36})\s*;\s*parameter\s+(?<role>width|length|thickness|holeDiameter|holeX|holeY)\s*=\s*(?<value>\d+(?:\.\d+)?)\s*mm\s*;\s*save as\s+(?<path>[^\r\n]+)\z",RegexOptions.CultureInvariant);
        public static bool IsRequest(string prompt)=>prompt?.TrimStart().StartsWith("V2 parametric edit:",StringComparison.OrdinalIgnoreCase)==true;
        public static CadParametricEditPromptRequest Parse(string prompt)
        {
            var match=Syntax.Match(prompt?.Trim()??"");
            if(!match.Success||!Guid.TryParse(match.Groups["id"].Value,out var id)||id==Guid.Empty||
                !double.TryParse(match.Groups["value"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||double.IsInfinity(value)||value<=0||
                !CadParametricEditContract.RelativePart(match.Groups["path"].Value.Trim()))
                throw new ArgumentException("Use: V2 parametric edit: job <GUID>; parameter width = 150 mm; save as edits/width.sldprt");
            return new CadParametricEditPromptRequest{JobId=id,Parameter=match.Groups["role"].Value,ValueMm=value,OutputPath=match.Groups["path"].Value.Trim()};
        }
    }
}
