namespace SolidWorksCadAgent.AgentHost.Host
{
    public sealed class AgentRequest
    {
        public AgentRequest(string method, string path, string body)
        {
            Method = method;
            Path = path;
            Body = body;
        }

        public string Method { get; }
        public string Path { get; }
        public string Body { get; }
    }
}
