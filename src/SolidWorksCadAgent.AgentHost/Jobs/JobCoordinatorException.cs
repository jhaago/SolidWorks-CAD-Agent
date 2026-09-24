using System;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    public sealed class JobCoordinatorException : Exception
    {
        public JobCoordinatorException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
