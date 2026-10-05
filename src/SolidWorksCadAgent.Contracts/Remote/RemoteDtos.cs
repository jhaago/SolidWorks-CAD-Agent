using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SolidWorksCadAgent.Contracts.Remote
{
    public static class RemoteJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            MissingMemberHandling = MissingMemberHandling.Error,
            MaxDepth = 16, DateParseHandling = DateParseHandling.DateTimeOffset
        };
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class RemoteSessionSnapshot
    {
        public string SessionId { get; set; }
        public long AuthorityEpoch { get; set; }
        public bool Controlling { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class RemoteCapturedFrame
    {
        public long FrameId { get; set; }
        public long DisplayGeneration { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTimeOffset CapturedAt { get; set; }
        public double CursorX { get; set; }
        public double CursorY { get; set; }
        public byte[] JpegBytes { get; set; }
    }

    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class RemoteInputEvent
    {
        public string SessionId { get; set; }
        public long AuthorityEpoch { get; set; }
        public long Sequence { get; set; }
        public long DisplayGeneration { get; set; }
        public string Kind { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public string Button { get; set; }
        public string Key { get; set; }
        public int Scroll { get; set; }

        public bool IsValid()
        {
            if (string.IsNullOrWhiteSpace(SessionId) || SessionId.Length > 128 ||
                AuthorityEpoch < 1 || Sequence < 1 || DisplayGeneration < 1 ||
                !Coordinate(X) || !Coordinate(Y)) return false;
            switch (Kind) {
                case "move": return true;
                case "down": case "up": case "click":
                    return Button == "primary" || Button == "secondary";
                case "scroll": return Scroll != 0 && Scroll >= -1200 && Scroll <= 1200;
                case "keyDown": case "keyUp": return AllowedKey(Key);
                default: return false;
            }
        }

        public static bool Coordinate(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 1;

        // Delete deliberately excluded so Ctrl+Alt+Delete cannot be expressed.
        public static bool AllowedKey(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 16) return false;
            if (key.Length == 1) return (key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9');
            return Keys.Contains(key);
        }

        private static readonly HashSet<string> Keys = new HashSet<string>(StringComparer.Ordinal) {
            "Enter", "Escape", "Tab", "Space", "Backspace", "Left", "Right", "Up", "Down",
            "Home", "End", "PageUp", "PageDown", "Shift", "Control", "Alt",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"
        };
    }

    public sealed class RemoteProtocolException : Exception
    {
        public int Status { get; }
        public string Code { get; }
        public RemoteProtocolException(int status, string code, string safeMessage) : base(safeMessage) { Status = status; Code = code; }
    }
}
