using System;

namespace SolidWorksCadAgent.Desktop.Api
{
    public sealed class AgentHostUnavailableException : Exception
    {
        public AgentHostUnavailableException(Exception innerException)
            : base("Agent Host unavailable. Start SolidWorksCadAgent.AgentHost and try again.", innerException)
        {
        }
    }
}
