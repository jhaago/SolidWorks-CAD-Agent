namespace SolidWorksCadAgent.AgentHost.Host
{
    public sealed class AgentResponse
    {
        public AgentResponse(int statusCode, string jsonBody)
        {
            StatusCode = statusCode;
            JsonBody = jsonBody;
        }

        public int StatusCode { get; }
        public string JsonBody { get; }
    }
}
