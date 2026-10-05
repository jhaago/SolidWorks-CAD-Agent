using System;

namespace SolidWorksCadAgent.AgentHost.Host
{
    public static class HostRequestPolicy
    {
        public const long MaximumBodyBytes = 1024 * 1024;

        public static AgentResponse Validate(
            string method,
            string contentType,
            string origin,
            long contentLength,
            bool hasEntityBody)
        {
            if (!string.IsNullOrWhiteSpace(origin))
            {
                return Error(403, "BROWSER_ORIGIN_REJECTED", "Browser-originated requests are not accepted by the local Agent Host.");
            }

            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var mediaType = (contentType ?? string.Empty).Split(';')[0].Trim();
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return Error(415, "JSON_REQUIRED", "POST requests must use application/json.");
            }

            if (hasEntityBody && contentLength < 0)
            {
                return Error(411, "CONTENT_LENGTH_REQUIRED", "Request bodies must provide a Content-Length header.");
            }

            if (contentLength > MaximumBodyBytes)
            {
                return Error(413, "REQUEST_TOO_LARGE", "The request body exceeds the one-megabyte limit.");
            }

            return null;
        }

        private static AgentResponse Error(int statusCode, string code, string message)
        {
            return new AgentResponse(
                statusCode,
                "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}");
        }
    }
}
