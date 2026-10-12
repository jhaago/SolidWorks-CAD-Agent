using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    /// <summary>Assigns Host-owned logical IDs to an untrusted, preflighted v2 candidate.</summary>
    public static class CadPlanV2HostNormalizer
    {
        public static CadPlanV2Document Normalize(CadPlanV2Document candidate)
        {
            var errors = CadPlanV2Preflight.Validate(candidate);
            if (errors.Count > 0)
                throw new ArgumentException("Only a valid unnormalized v2 candidate can be normalized: " + string.Join(" ", errors), nameof(candidate));

            var entityIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var step in candidate.Steps.Where(step => step.Command == CadCommandNames.CreateSketch))
            {
                Guid entityId;
                do { entityId = Guid.NewGuid(); }
                while (entityId == Guid.Empty || entityIds.Values.Contains(entityId));
                entityIds.Add(step.OutputKey, entityId);
            }

            var normalized = new CadPlanV2Document
            {
                PlanVersion = candidate.PlanVersion,
                Summary = candidate.Summary,
                Assumptions = candidate.Assumptions == null ? null : new List<string>(candidate.Assumptions),
                Ambiguities = candidate.Ambiguities == null ? null : new List<string>(candidate.Ambiguities),
                Steps = new List<CadPlanV2Step>(candidate.Steps.Count)
            };

            foreach (var step in candidate.Steps)
            {
                var copy = new CadPlanV2Step
                {
                    StepKey = step.StepKey,
                    Command = step.Command,
                    OperationVersion = step.OperationVersion,
                    Parameters = step.Parameters == null ? null : (JObject)step.Parameters.DeepClone(),
                    OutputKey = step.OutputKey,
                    OutputEntityId = step.Command == CadCommandNames.CreateSketch
                        ? entityIds[step.OutputKey]
                        : (Guid?)null,
                    Inputs = step.Inputs == null ? null : new CadPlanV2Inputs
                    {
                        ProfileSketch = step.Inputs.ProfileSketch == null ? null : new CadPlanV2Reference
                        {
                            Kind = step.Inputs.ProfileSketch.Kind,
                            OutputKey = step.Inputs.ProfileSketch.OutputKey,
                            EntityId = entityIds[step.Inputs.ProfileSketch.OutputKey]
                        }
                    }
                };
                normalized.Steps.Add(copy);
            }

            var normalizedErrors = CadPlanV2Preflight.Validate(normalized, true);
            if (normalizedErrors.Count > 0)
                throw new InvalidOperationException("The Host produced a non-canonical v2 plan: " + string.Join(" ", normalizedErrors));
            return normalized;
        }
    }
}
