using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>Fail-closed comparison of persisted plan expectations with independent native cut readback.</summary>
    public static class CadV2NativeCutReadbackMatcher
    {
        private const double ToleranceMm = 0.02;

        public static bool Matches(IReadOnlyList<CadV2CutExpectation> expected, JArray observed, out string reason)
        {
            reason = null;
            if (expected == null || observed == null)
                return Fail("Native cut readback is missing.", out reason);
            if (expected.Count != observed.Count)
                return Fail("Native cut count differs from the persisted plan.", out reason);

            for (int index = 0; index < expected.Count; index++)
            {
                var plan = expected[index];
                var actual = observed[index] as JObject;
                if (plan == null || actual == null || string.IsNullOrWhiteSpace(actual.Value<string>("featureName")))
                    return Fail("Native cut feature identity is missing at index " + index + ".", out reason);
                var profile = actual["profile"] as JObject;
                if (profile == null || profile.Value<bool?>("parentRelationVerified") != true ||
                    string.IsNullOrWhiteSpace(profile.Value<string>("sketchName")))
                    return Fail("Native cut parent profile sketch was not independently identified at index " + index + ".", out reason);
                if (!string.Equals(actual.Value<string>("endCondition"), plan.EndCondition, StringComparison.Ordinal) ||
                    actual.Value<bool?>("reverseDirection") != true || actual.Value<bool?>("flipSideToCut") != false ||
                    actual.Value<bool?>("bothDirections") != false)
                    return Fail("Native cut condition or direction differs from the executed V2 cut contract at index " + index + ".", out reason);

                var actualDepth = actual["depthMm"];
                if (plan.DepthMm.HasValue)
                {
                    if (!ReadFinite(actualDepth, out var depth) || Math.Abs(depth - plan.DepthMm.Value) > ToleranceMm)
                        return Fail("Native blind cut depth differs from the persisted plan at index " + index + ".", out reason);
                }
                else if (actualDepth == null || actualDepth.Type != JTokenType.Null)
                    return Fail("Through-all native cut unexpectedly reports a blind depth at index " + index + ".", out reason);

                if (profile.Value<int?>("lineCount") != plan.LineCount || profile.Value<int?>("arcCount") != plan.ArcCount ||
                    !Close(profile, "minXmm", plan.MinXmm) || !Close(profile, "maxXmm", plan.MaxXmm) ||
                    !Close(profile, "minYmm", plan.MinYmm) || !Close(profile, "maxYmm", plan.MaxYmm))
                    return Fail("Native parent sketch position or topology differs from the persisted plan at index " + index + ".", out reason);
                if (!MatchesSegments(plan.Segments, profile["segments"] as JArray))
                    return Fail("Native parent sketch segment geometry differs from the persisted plan at index " + index + ".", out reason);
            }
            return true;
        }

        private static bool MatchesSegments(IReadOnlyList<CadV2ProfileSegmentExpectation> expected, JArray observed)
        {
            if (expected == null || observed == null || expected.Count != observed.Count) return false;
            var used = new bool[observed.Count];
            foreach (var segment in expected)
            {
                bool found = false;
                for (int index = 0; index < observed.Count; index++)
                {
                    if (used[index] || !MatchesSegment(segment, observed[index] as JObject)) continue;
                    used[index] = true;
                    found = true;
                    break;
                }
                if (!found) return false;
            }
            return true;
        }

        private static bool MatchesSegment(CadV2ProfileSegmentExpectation expected, JObject observed)
        {
            if (expected == null || observed == null ||
                !string.Equals(observed.Value<string>("type"), expected.Type, StringComparison.Ordinal)) return false;
            if (expected.Type == "Line")
            {
                bool forward = Close(observed, "startXmm", expected.StartXmm) && Close(observed, "startYmm", expected.StartYmm) &&
                    Close(observed, "endXmm", expected.EndXmm) && Close(observed, "endYmm", expected.EndYmm);
                bool reverse = Close(observed, "startXmm", expected.EndXmm) && Close(observed, "startYmm", expected.EndYmm) &&
                    Close(observed, "endXmm", expected.StartXmm) && Close(observed, "endYmm", expected.StartYmm);
                return forward || reverse;
            }
            if (expected.Type != "Arc" || observed.Value<bool?>("isCircle") != expected.IsCircle ||
                !Close(observed, "centerXmm", expected.CenterXmm) || !Close(observed, "centerYmm", expected.CenterYmm) ||
                !Close(observed, "radiusMm", expected.RadiusMm)) return false;
            if (expected.IsCircle) return true;

            int? direction = observed.Value<int?>("rotationDirection");
            if (!direction.HasValue || direction.Value == 0) return false;
            bool forwardEndpoints = Close(observed, "startXmm", expected.StartXmm) && Close(observed, "startYmm", expected.StartYmm) &&
                Close(observed, "endXmm", expected.EndXmm) && Close(observed, "endYmm", expected.EndYmm);
            bool reverseEndpoints = Close(observed, "startXmm", expected.EndXmm) && Close(observed, "startYmm", expected.EndYmm) &&
                Close(observed, "endXmm", expected.StartXmm) && Close(observed, "endYmm", expected.StartYmm);
            return forwardEndpoints && IsDirection(direction.Value, expected.Clockwise) ||
                reverseEndpoints && IsDirection(direction.Value, !expected.Clockwise);
        }

        private static bool IsDirection(int nativeDirection, bool clockwise) => (nativeDirection < 0) == clockwise;

        private static bool Close(JObject data, string key, double expected) =>
            ReadFinite(data?[key], out var actual) && Math.Abs(actual - expected) <= ToleranceMm;

        private static bool ReadFinite(JToken token, out double value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer && token.Type != JTokenType.Float) return false;
            value = token.Value<double>();
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool Fail(string message, out string reason) { reason = message; return false; }
    }
}
