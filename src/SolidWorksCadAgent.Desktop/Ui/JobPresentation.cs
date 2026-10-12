using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Desktop.Api;

namespace SolidWorksCadAgent.Desktop.Ui
{
    public static class JobPresentation
    {
        public static bool CanApprove(JobViewDto job) => job != null && job.State == "AwaitingApproval" &&
            job.PlanValidated && !job.HasUnresolvedAmbiguity && job.CurrentRevisionId.HasValue;
        public static bool CanRevise(JobViewDto job) => job != null && job.CurrentRevisionId.HasValue &&
            !((int?)Property(job.Plan, "planVersion") == 2 && job.PlanValidated) &&
            (job.State == "AwaitingApproval" || job.State == "AwaitingClarification");
        public static bool CanCancel(JobViewDto job) => job != null &&
            job.State != "Cancelled" && job.State != "ReadyForReview" && job.State != "Completed" && job.State != "Failed";
        public static bool IsActive(JobViewDto job) => job != null &&
            (job.State == "Planning" || job.State == "Interpreting" || job.State == "Approved" || job.State == "Executing" || job.State == "Verifying");

        public static string StateTitle(string state) => Regex.Replace(state ?? "No job selected", "([a-z])([A-Z])", "$1 $2");

        public static string Guidance(JobViewDto job)
        {
            switch (job?.State)
            {
                case "AwaitingApproval": return "Review the geometry, dimensions and operations, then approve this revision to build.";
                case "AwaitingClarification": return "Resolve the missing design details in the composer and select Request changes.";
                case "Planning": case "Interpreting": return "The planner is interpreting the request. The host will validate the proposed operations.";
                case "Approved": case "Executing": return "CAD operations are running. Cancellation stops subsequent commands after the current operation.";
                case "Verifying": return "The host is checking body count, bounds and rebuild errors.";
                case "ReadyForReview": return job.IsSimulated ? "Simulation verified. No native SolidWorks model was created." : "Verification passed. Inspect the resulting model in SolidWorks.";
                case "Cancelled": return "This job was cancelled. Review activity for operations completed before cancellation.";
                case "Completed": return "This job was accepted and completed. Submit a new request for further work.";
                case "Failed": return "The job stopped. Review activity and diagnostics before submitting a revised request.";
                default: return "Describe the part, dimensions and feature intent. The agent will propose a plan for review.";
            }
        }

        public static JToken Property(JToken token, string name) => (token as JObject)?.GetValue(name, StringComparison.OrdinalIgnoreCase);
        public static string Value(JToken token, string name) => Property(token, name)?.ToString() ?? string.Empty;

        public static string Brief(JobViewDto job)
        {
            if (job?.Plan == null) return "The design interpretation will appear here after planning.";
            var text = Value(job.Plan, "summary");
            var assumptions = Property(job.Plan, "assumptions") as JArray;
            if (assumptions?.Count > 0) text += "\r\n\r\nAssumptions\r\n" + string.Join("\r\n", assumptions.Select(item => "• " + item));
            var provider = Value(job.Plan, "provider");
            var model = Value(job.Plan, "model");
            if (!string.IsNullOrWhiteSpace(provider)) text += "\r\n\r\nPlanner: " + provider + " / " + model;
            return Redact(text);
        }

        public static IEnumerable<OperationRow> Operations(JobViewDto job)
        {
            var proposed = (int?)Property(job?.Plan, "planVersion") == 2
                ? Property(job.Plan, "steps") as JArray
                : Property(job?.Plan, "proposedCommands") as JArray;
            if (proposed == null) yield break;
            var sequence = 0;
            foreach (var command in proposed)
            {
                sequence++;
                var record = job.Commands?.FirstOrDefault(item => (int?)Property(item, "sequenceNumber") == sequence &&
                    (int?)Property(item, "revisionNumber") == job.CurrentRevisionNumber);
                yield return new OperationRow { Sequence = sequence, Operation = Value(command, "command"),
                    Status = record == null ? "Planned" : (bool?)Property(record, "success") == true ? "Completed" : "Failed",
                    Detail = SafeJson(Property(command, "parameters"), Formatting.None) };
            }
        }

        public static IEnumerable<OperationRow> Activity(JobViewDto job)
        {
            foreach (var item in job?.Commands ?? new JArray())
                yield return new OperationRow { Sequence = (int?)Property(item, "sequenceNumber") ?? 0,
                    Operation = Value(item, "commandName"), Status = (bool?)Property(item, "success") == true ? "Completed" : "Failed",
                    Detail = "Revision " + Value(item, "revisionNumber") + " · " + Redact(Value(item, "errorMessage")) +
                        " " + SafeJson(Property(item, "result"), Formatting.None) };
            foreach (var item in job?.Verifications ?? new JArray())
                yield return new OperationRow { Operation = Value(item, "checkName"),
                    Status = (bool?)Property(item, "passed") == true ? "Passed" : "Failed",
                    Detail = "Revision " + Value(item, "revisionNumber") + " · " + SafeJson(Property(item, "actual"), Formatting.None) };
        }

        public static string SafeJson(JToken token, Formatting formatting = Formatting.Indented)
        {
            if (token == null) return string.Empty;
            var copy = token.DeepClone();
            Mask(copy);
            return Redact(copy.ToString(formatting));
        }

        private static void Mask(JToken token)
        {
            if (token is JObject obj)
                foreach (var property in obj.Properties().ToArray())
                {
                    var name = Regex.Replace(property.Name, "[^a-zA-Z]", "").ToLowerInvariant();
                    if (name.Contains("apikey") || name.Contains("secret") || name.Contains("password") || name == "authorization" || name == "accesstoken")
                        property.Value = "[redacted]";
                    else Mask(property.Value);
                }
            else if (token is JArray array) foreach (var item in array) Mask(item);
        }

        public static string Redact(string value) => Regex.Replace(value ?? string.Empty,
            @"\bsk-[A-Za-z0-9_-]{8,}|\bBearer\s+[^\s""\\]+", "[redacted]", RegexOptions.IgnoreCase);
    }

    public sealed class OperationRow
    {
        public int Sequence { get; set; }
        public string Operation { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
    }
}
