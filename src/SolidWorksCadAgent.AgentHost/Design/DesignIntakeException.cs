using System;
namespace SolidWorksCadAgent.AgentHost.Design
{
    public sealed class DesignIntakeException : Exception
    {

        public string Code
        {
            get;
        }

        public DesignIntakeException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}


