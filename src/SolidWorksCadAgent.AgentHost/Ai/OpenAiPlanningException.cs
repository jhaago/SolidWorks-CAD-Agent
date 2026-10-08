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

        public OpenAiPlanningException(string code, string message, int httpStatusCode, string openAiErrorCode, string openAiErrorType)
            : this(code, message)
        {
            HttpStatusCode = httpStatusCode;
            OpenAiErrorCode = openAiErrorCode;
            OpenAiErrorType = openAiErrorType;
        }

        public string Code { get; }
        public int? HttpStatusCode { get; }
        public string OpenAiErrorCode { get; }
        public string OpenAiErrorType { get; }
    }
}
