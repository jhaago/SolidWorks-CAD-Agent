using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    /// <summary>
    /// Constructs one execution-only request from an immutable Host-normalized revision.
    /// Approval and whole-plan admission remain responsibilities of JobCoordinator.
    /// </summary>
    public static class CadPlanV2ExecutionBinder
    {
        public static CadVersionedCommandRequest BindExtrude(
            string persistedPlanJson, string stepKey, Guid executionId, Guid managedModelId)
        {
            var request = BindFeature(persistedPlanJson, stepKey, executionId, managedModelId);
            if (request.Command.Command != CadCommandNames.Extrude)
                throw new ArgumentException("The selected step is not an Extrude operation.", nameof(stepKey));
            return request;
        }

        public static CadVersionedCommandRequest BindFeature(
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
            if (step == null || (step.Command != CadCommandNames.Extrude && step.Command != CadCommandNames.CutExtrude) ||
                (step.OperationVersion != 2 && !(step.Command == CadCommandNames.Extrude && step.OperationVersion == 3)) ||
                step.Inputs?.ProfileSketch?.EntityId == null)
                throw new ArgumentException("The selected step is not a normalized version-2 feature operation.", nameof(stepKey));

            return new CadVersionedCommandRequest(2, step.OperationVersion.Value, new CadCommandEnvelope
            {
                Command = step.Command,
                Parameters = (JObject)step.Parameters.DeepClone(),
                ExecutionId = executionId,
                ManagedModelId = managedModelId
            }, step.Inputs.ProfileSketch.EntityId.Value);
        }
    }
}
