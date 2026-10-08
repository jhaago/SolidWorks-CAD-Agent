using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>Validates candidate-only v2 operation versions and plan-local sketch dependencies.</summary>
    public static class CadPlanV2Preflight
    {
        private sealed class SketchOutput
        {
            internal bool Closed;
            internal bool HasProfile;
            internal bool Consumed;
        }

        public static IReadOnlyList<string> Validate(CadPlanV2Document plan) => Validate(plan, false);

        public static IReadOnlyList<string> Validate(CadPlanV2Document plan, bool requireNormalizedOutputIds)
        {
            var errors = new List<string>();
            if (plan == null) return new[] { "INVALID_PLAN: The version-2 plan is missing." };
            if (plan.PlanVersion != 2) errors.Add("UNSUPPORTED_PLAN_VERSION: Candidate plans must declare planVersion 2.");
            if (plan.AdditionalProperties != null && plan.AdditionalProperties.Count > 0)
                errors.Add("INVALID_PLAN: Unexpected version-2 root property '" + plan.AdditionalProperties.Keys.First() + "'.");
            if (plan.Steps == null || plan.Steps.Count == 0)
            {
                errors.Add("INVALID_PLAN: A version-2 candidate must contain at least one step.");
                return errors;
            }

            var stepKeys = new HashSet<string>(StringComparer.Ordinal);
            var outputSteps = new Dictionary<string, int>(StringComparer.Ordinal);
            var outputEntityIds = new HashSet<Guid>();
            var envelopes = new List<CadCommandEnvelope>();
            var hasNewPartAtStart = plan.Steps[0] != null && plan.Steps[0].Command == CadCommandNames.NewPart;
            var newPartCount = 0;

            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                if (step == null)
                {
                    errors.Add("STEP_" + (i + 1) + ": Step cannot be null.");
                    continue;
                }
                var index = i + 1;
                if (string.IsNullOrWhiteSpace(step.StepKey)) errors.Add("STEP_" + index + ": stepKey is required.");
                else if (!stepKeys.Add(step.StepKey)) errors.Add("STEP_" + index + ": Duplicate stepKey '" + step.StepKey + "'.");
                if (step.AdditionalProperties != null && step.AdditionalProperties.Count > 0)
                    errors.Add("STEP_" + index + ": Unexpected property '" + step.AdditionalProperties.Keys.First() + "'.");
                if (string.IsNullOrWhiteSpace(step.Command))
                {
                    errors.Add("STEP_" + index + ": command is required.");
                    continue;
                }

                if (step.Command == CadCommandNames.NewPart) newPartCount++;
                if (step.Command == CadCommandNames.OpenPart)
                    errors.Add("STEP_" + index + ": Version-2 sketch references currently require a plan-owned NewPart; OpenPart references are unsupported.");
                if (step.OperationVersion == null)
                    errors.Add("STEP_" + index + ": operationVersion is required.");
                else
                {
                    var descriptor = CadOperationCatalog.Find(step.Command);
                    if (descriptor == null)
                        errors.Add("STEP_" + index + ": Unsupported command '" + step.Command + "'.");
                    else
                    {
                        var expectedVersion = IsFeatureConsumer(step.Command) ? 2 : descriptor.OperationVersion;
                        if (step.OperationVersion.Value != expectedVersion)
                            errors.Add("STEP_" + index + ": Unsupported operationVersion " + step.OperationVersion.Value + " for " + step.Command + "; expected " + expectedVersion + ".");
                    }
                }

                if (step.Parameters == null)
                    errors.Add("STEP_" + index + ": parameters must be a JSON object.");
                else if (DescriptorAvailable(step.Command))
                {
                    var parameterError = CadPlanningCommandContract.Validate(new CadCommandEnvelope
                    {
                        Command = step.Command,
                        Parameters = step.Parameters
                    });
                    if (parameterError != null) errors.Add("STEP_" + index + ": " + parameterError);
                    envelopes.Add(new CadCommandEnvelope { Command = step.Command, Parameters = step.Parameters });
                }

                if (step.Command == CadCommandNames.CreateSketch)
                {
                    if (string.IsNullOrWhiteSpace(step.OutputKey)) errors.Add("STEP_" + index + ": CreateSketch requires a non-empty outputKey.");
                    else if (outputSteps.ContainsKey(step.OutputKey)) errors.Add("STEP_" + index + ": Duplicate outputKey '" + step.OutputKey + "'.");
                    else outputSteps.Add(step.OutputKey, i);
                    if (requireNormalizedOutputIds)
                    {
                        if (!step.OutputEntityId.HasValue || step.OutputEntityId.Value == Guid.Empty)
                            errors.Add("STEP_" + index + ": Persisted CreateSketch requires a non-empty Host outputEntityId.");
                        else if (!outputEntityIds.Add(step.OutputEntityId.Value))
                            errors.Add("STEP_" + index + ": Duplicate Host outputEntityId.");
                    }
                    else if (step.OutputEntityId.HasValue)
                        errors.Add("STEP_" + index + ": Candidate CreateSketch cannot assign Host outputEntityId.");
                }
                else
                {
                    if (step.OutputKey != null) errors.Add("STEP_" + index + ": Only CreateSketch may declare an outputKey.");
                    if (step.OutputEntityId.HasValue) errors.Add("STEP_" + index + ": Only CreateSketch may declare outputEntityId.");
                }

                if (!IsFeatureConsumer(step.Command) && step.Inputs != null)
                    errors.Add("STEP_" + index + ": Only Extrude and CutExtrude may declare inputs.");
                if (step.Inputs?.AdditionalProperties != null && step.Inputs.AdditionalProperties.Count > 0)
                    errors.Add("STEP_" + index + ": Unexpected input name '" + step.Inputs.AdditionalProperties.Keys.First() + "'.");
                if (step.Inputs?.ProfileSketch?.AdditionalProperties != null && step.Inputs.ProfileSketch.AdditionalProperties.Count > 0)
                    errors.Add("STEP_" + index + ": Unexpected profileSketch property '" + step.Inputs.ProfileSketch.AdditionalProperties.Keys.First() + "'.");
            }

            if (!hasNewPartAtStart || newPartCount != 1)
                errors.Add("PLAN: Version-2 reference candidates must start with exactly one NewPart.");

            var outputs = new Dictionary<string, SketchOutput>(StringComparer.Ordinal);
            string activeSketchKey = null;
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                if (step == null || string.IsNullOrWhiteSpace(step.Command)) continue;
                var index = i + 1;

                if (step.Command == CadCommandNames.CreateSketch)
                {
                    if (activeSketchKey != null)
                        errors.Add("STEP_" + index + ": ExitSketch before creating another sketch.");
                    if (!string.IsNullOrWhiteSpace(step.OutputKey) && !outputs.ContainsKey(step.OutputKey))
                    {
                        outputs.Add(step.OutputKey, new SketchOutput());
                        activeSketchKey = step.OutputKey;
                    }
                }
                else if (IsProfilePrimitive(step.Command))
                {
                    if (activeSketchKey == null)
                        errors.Add("STEP_" + index + ": Profile geometry requires an open sketch step.");
                    else outputs[activeSketchKey].HasProfile = true;
                }
                else if (step.Command == CadCommandNames.ExitSketch)
                {
                    if (activeSketchKey == null)
                        errors.Add("STEP_" + index + ": ExitSketch has no open sketch.");
                    else
                    {
                        outputs[activeSketchKey].Closed = true;
                        activeSketchKey = null;
                    }
                }
                else if (IsFeatureConsumer(step.Command))
                {
                    var reference = step.Inputs?.ProfileSketch;
                    if (reference == null)
                    {
                        errors.Add("STEP_" + index + ": " + step.Command + " requires inputs.profileSketch.");
                        continue;
                    }
                    if (reference.Kind != "Sketch") errors.Add("STEP_" + index + ": profileSketch kind must be Sketch.");
                    if (requireNormalizedOutputIds)
                    {
                        if (!reference.EntityId.HasValue || reference.EntityId.Value == Guid.Empty)
                            errors.Add("STEP_" + index + ": Persisted profileSketch requires a non-empty Host entityId.");
                    }
                    else if (reference.EntityId.HasValue)
                        errors.Add("STEP_" + index + ": Candidate profileSketch cannot assign Host entityId.");
                    if (string.IsNullOrWhiteSpace(reference.OutputKey))
                    {
                        errors.Add("STEP_" + index + ": profileSketch.outputKey is required.");
                        continue;
                    }
                    if (!outputSteps.TryGetValue(reference.OutputKey, out var producerIndex))
                    {
                        errors.Add("STEP_" + index + ": Dangling profile sketch outputKey '" + reference.OutputKey + "'.");
                        continue;
                    }
                    if (producerIndex >= i)
                    {
                        errors.Add("STEP_" + index + ": profileSketch must reference a sketch created by an earlier step.");
                        continue;
                    }
                    if (requireNormalizedOutputIds && plan.Steps[producerIndex].OutputEntityId != reference.EntityId)
                        errors.Add("STEP_" + index + ": profileSketch entityId does not match the Host ID assigned to its outputKey.");
                    if (!outputs.TryGetValue(reference.OutputKey, out var sketch))
                    {
                        errors.Add("STEP_" + index + ": The referenced sketch output is not available yet.");
                        continue;
                    }
                    if (!sketch.Closed) errors.Add("STEP_" + index + ": The referenced sketch must be closed before feature creation.");
                    if (!sketch.HasProfile) errors.Add("STEP_" + index + ": The referenced sketch has no supported closed-profile primitive.");
                    if (sketch.Consumed) errors.Add("STEP_" + index + ": A sketch output may be consumed by only one feature in this initial contract.");
                    sketch.Consumed = true;
                }
            }

            if (activeSketchKey != null)
                errors.Add("PLAN: Exit the final open sketch before the plan ends.");
            errors.AddRange(CadPlanLifecycleValidator.ValidateReferenceAware(envelopes));
            return errors;
        }

        private static bool DescriptorAvailable(string command) => CadOperationCatalog.Find(command) != null;
        private static bool IsFeatureConsumer(string command) => command == CadCommandNames.Extrude || command == CadCommandNames.CutExtrude;
        private static bool IsProfilePrimitive(string command) => command == CadCommandNames.AddRectangle ||
            command == CadCommandNames.AddCircle || command == CadCommandNames.AddSlot || command == CadCommandNames.AddRegularPolygon;
    }
}
