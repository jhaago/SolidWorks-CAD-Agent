using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    /// <summary>
    /// Constructs one execution-only request from an immutable Host-normalized revision.
    /// This does not approve or execute the plan; JobCoordinator's v2 gate remains closed.
    /// </summary>
    public static class CadPlanV2ExecutionBinder
    {
        public static CadVersionedCommandRequest BindExtrude(
            string persistedPlanJson, string stepKey, Guid executionId, Guid managedModelId)
        {
            if (executionId == Guid.Empty || managedModelId == Guid.Empty)
                throw new ArgumentException("Host execution and managed-model IDs are required.");
            if (string.IsNullOrWhiteSpace(stepKey))
                throw new ArgumentException("A version-2 step key is required.", nameof(stepKey));

            var parsed = CadPlanDocumentReader.ReadPersisted(persistedPlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null)
                throw new ArgumentException("A valid Host-normalized version-2 revision is required: " +
                    string.Join(" ", parsed.Errors), nameof(persistedPlanJson));

            var step = parsed.CandidateV2.Steps.SingleOrDefault(item =>
                string.Equals(item.StepKey, stepKey, StringComparison.Ordinal));
            if (step == null || step.Command != CadCommandNames.Extrude || step.OperationVersion != 2 ||
                step.Inputs?.ProfileSketch?.EntityId == null)
                throw new ArgumentException("The selected step is not a normalized version-2 Extrude operation.", nameof(stepKey));

            return new CadVersionedCommandRequest(2, 2, new CadCommandEnvelope
            {
                Command = CadCommandNames.Extrude,
                Parameters = (JObject)step.Parameters.DeepClone(),
                ExecutionId = executionId,
                ManagedModelId = managedModelId
            }, step.Inputs.ProfileSketch.EntityId.Value);
        }
    }
}
