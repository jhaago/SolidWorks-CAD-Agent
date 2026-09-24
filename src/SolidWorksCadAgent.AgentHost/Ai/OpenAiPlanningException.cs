using System;

namespace SolidWorksCadAgent.AgentHost.Ai
{
    public sealed class OpenAiPlanningException : Exception
    {
        public OpenAiPlanningException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public OpenAiPlanningException(string code, string message, Exception innerException)
            : base(message, innerException)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
