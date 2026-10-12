using System;

namespace SolidWorksCadAgent.Desktop.Api
{
    public sealed class AgentHostApiException : Exception
    {
        public AgentHostApiException(int statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public int StatusCode { get; }
    }
}
