using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>Dispatches historical unversioned plans and explicit version-2 candidates without fallback.</summary>
    public static class CadPlanDocumentReader
    {
        public static CadPlanDocumentReadResult Read(string json)
        {
            return ReadCandidate(json);
        }

        public static CadPlanDocumentReadResult ReadCandidate(string json) => ReadCore(json, false);

        /// <summary>Reads an immutable persisted plan. V2 must already carry Host-normalized output IDs.</summary>
        public static CadPlanDocumentReadResult ReadPersisted(string json) => ReadCore(json, true);

        private static CadPlanDocumentReadResult ReadCore(string json, bool requireNormalizedV2)
        {
            if (string.IsNullOrWhiteSpace(json))
                return CadPlanDocumentReadResult.Fail(null, "INVALID_PLAN_DOCUMENT: Plan JSON is empty.");

            JObject root;
            try
            {
                root = JObject.Parse(json, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            {
                return CadPlanDocumentReadResult.Fail(null, "INVALID_PLAN_DOCUMENT: " + ex.Message);
            }

            var versionProperty = root.Properties().FirstOrDefault(property =>
                string.Equals(property.Name, "planVersion", StringComparison.OrdinalIgnoreCase));
            if (versionProperty == null)
            {
                if (root.Properties().Any(property => string.Equals(property.Name, "steps", StringComparison.OrdinalIgnoreCase)))
                    return CadPlanDocumentReadResult.Fail(null, "PLAN_VERSION_REQUIRED: A steps-based plan must declare planVersion.");
                try
                {
                    var legacy = root.ToObject<CadPlanningResult>();
                    return legacy == null
                        ? CadPlanDocumentReadResult.Fail(1, "INVALID_PLAN_DOCUMENT: The version-1 plan is null.")
                        : CadPlanDocumentReadResult.Legacy(legacy);
                }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
                {
                    return CadPlanDocumentReadResult.Fail(1, "INVALID_PLAN_DOCUMENT: " + ex.Message);
                }
            }

            if (versionProperty.Name != "planVersion" || versionProperty.Value.Type != JTokenType.Integer)
                return CadPlanDocumentReadResult.Fail(null, "PLAN_VERSION_INVALID: planVersion must be the exact integer field name.");

            int version;
            try { version = versionProperty.Value.Value<int>(); }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException || ex is InvalidCastException)
            { return CadPlanDocumentReadResult.Fail(null, "PLAN_VERSION_INVALID: planVersion is outside the supported integer range."); }
            if (version != 2)
                return CadPlanDocumentReadResult.Fail(version, "UNSUPPORTED_PLAN_VERSION: Version 2 is accepted only as normalized review data; execution is unsupported.");

            var aliasError = ValidateV2PropertyNames(root);
            if (aliasError != null)
                return CadPlanDocumentReadResult.Fail(version, aliasError);

            CadPlanV2Document candidate;
            try { candidate = root.ToObject<CadPlanV2Document>(); }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException)
            { return CadPlanDocumentReadResult.Fail(version, "INVALID_PLAN_DOCUMENT: " + ex.Message); }

            var errors = CadPlanV2Preflight.Validate(candidate, requireNormalizedV2);
            return errors.Count == 0
                ? CadPlanDocumentReadResult.Candidate(candidate)
                : CadPlanDocumentReadResult.Fail(version, errors);
        }

        private static string ValidateV2PropertyNames(JObject root)
        {
            var duplicate = FindCaseInsensitiveDuplicate(root, "$");
            if (duplicate != null) return duplicate;

            var error = FindSchemaAlias(root, "$", "planVersion", "summary", "assumptions", "ambiguities", "steps");
            if (error != null) return error;
            if (!(root["steps"] is JArray steps)) return null;
            for (var index = 0; index < steps.Count; index++)
            {
                if (!(steps[index] is JObject step)) continue;
                var path = "$.steps[" + index + "]";
                error = FindSchemaAlias(step, path, "stepKey", "command", "operationVersion", "parameters",
                    "outputKey", "outputEntityId", "inputs");
                if (error != null) return error;
                if (!(step["inputs"] is JObject inputs)) continue;
                error = FindSchemaAlias(inputs, path + ".inputs", "profileSketch");
                if (error != null) return error;
                if (inputs["profileSketch"] is JObject reference)
                {
                    error = FindSchemaAlias(reference, path + ".inputs.profileSketch", "kind", "outputKey", "entityId");
                    if (error != null) return error;
                }
            }
            return null;
        }

        private static string FindCaseInsensitiveDuplicate(JToken token, string path)
        {
            if (token is JObject obj)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in obj.Properties())
                {
                    if (!names.Add(property.Name))
                        return "INVALID_PLAN_DOCUMENT: Duplicate case-insensitive property '" + property.Name + "' at " + path + ".";
                    var error = FindCaseInsensitiveDuplicate(property.Value, path + "." + property.Name);
                    if (error != null) return error;
                }
            }
            else if (token is JArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    var error = FindCaseInsensitiveDuplicate(array[index], path + "[" + index + "]");
                    if (error != null) return error;
                }
            }
            return null;
        }

        private static string FindSchemaAlias(JObject obj, string path, params string[] canonicalNames)
        {
            foreach (var property in obj.Properties())
            {
                var canonical = canonicalNames.FirstOrDefault(name =>
                    string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase));
                if (canonical != null && !string.Equals(canonical, property.Name, StringComparison.Ordinal))
                    return "INVALID_PLAN_DOCUMENT: Property '" + property.Name + "' at " + path +
                        " must use exact spelling '" + canonical + "'.";
            }
            return null;
        }
    }

    public sealed class CadPlanDocumentReadResult
    {
        private CadPlanDocumentReadResult(int? version, CadPlanningResult legacy, CadPlanV2Document candidate, IReadOnlyList<string> errors)
        {
            Version = version;
            LegacyPlan = legacy;
            CandidateV2 = candidate;
            Errors = errors;
        }

        public int? Version { get; }
        public CadPlanningResult LegacyPlan { get; }
        public CadPlanV2Document CandidateV2 { get; }
        public IReadOnlyList<string> Errors { get; }
        public bool IsValid => Errors.Count == 0;

        internal static CadPlanDocumentReadResult Legacy(CadPlanningResult plan) =>
            new CadPlanDocumentReadResult(1, plan, null, Array.Empty<string>());
        internal static CadPlanDocumentReadResult Candidate(CadPlanV2Document plan) =>
            new CadPlanDocumentReadResult(2, null, plan, Array.Empty<string>());
        internal static CadPlanDocumentReadResult Fail(int? version, params string[] errors) =>
            new CadPlanDocumentReadResult(version, null, null, errors ?? Array.Empty<string>());
        internal static CadPlanDocumentReadResult Fail(int? version, IReadOnlyList<string> errors) =>
            new CadPlanDocumentReadResult(version, null, null, errors ?? Array.Empty<string>());
    }
}
