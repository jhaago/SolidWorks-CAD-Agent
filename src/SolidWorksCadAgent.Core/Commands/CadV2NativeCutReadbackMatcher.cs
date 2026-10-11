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
            }
            return true;
        }

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
