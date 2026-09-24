using System;

namespace SolidWorksCadAgent.AgentHost.Host
{
    public static class HostPrefixPolicy
    {
        public static Uri Validate(string prefix)
        {
            if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) ||
                uri.IsDefaultPort ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException(
                    "The V1 Agent Host prefix must be an HTTP URL bound exactly to 127.0.0.1 with an explicit port and root path.",
                    nameof(prefix));
            }

            return uri;
        }
    }
}
