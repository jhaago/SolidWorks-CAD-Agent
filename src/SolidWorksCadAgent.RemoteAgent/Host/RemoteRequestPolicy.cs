using System;
using System.Net;
using System.Text;

namespace SolidWorksCadAgent.RemoteAgent.Host
{
    public static class RemoteRequestPolicy
    {
        public const int BodyLimit=65536, FrameResponseLimit=3145728, ArtifactResponseLimit=6291456;
        public static bool IsAllowedPrefix(Uri uri) => uri!=null && uri.IsAbsoluteUri && uri.Scheme=="http" &&
            uri.Host=="127.0.0.1" && uri.Port>=1024 && uri.AbsolutePath=="/" &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
        public static bool IsBodyBounded(string body) => body!=null && Encoding.UTF8.GetByteCount(body)<=BodyLimit;
    }
    public sealed class RemoteRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public string Authorization { get; set; }
        public string DeviceCredential { get; set; }
        public string ContentType { get; set; }
        public string Body { get; set; }
    }
    public sealed class RemoteResponse
    {
        public int Status { get; set; }
        public string Body { get; set; }
    }
}
