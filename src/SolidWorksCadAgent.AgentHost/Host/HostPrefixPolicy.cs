using System;
using System.Text.RegularExpressions;

namespace SolidWorksCadAgent.AgentHost.Host
{
    public static class HostPrefixPolicy
    {
        private static readonly Regex ExactLoopbackPrefix = new Regex(
            @"^http://127\.0\.0\.1:[0-9]{1,5}/$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static Uri Validate(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix) ||
                !ExactLoopbackPrefix.IsMatch(prefix) ||
                !Uri.TryCreate(prefix, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.IsDefaultPort ||
                uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException(
                    "The V1 Agent Host prefix must be an HTTP URL bound exactly to 127.0.0.1 with an explicit non-default port and root path.",
                    nameof(prefix));
            }

            return uri;
        }
    }
}
