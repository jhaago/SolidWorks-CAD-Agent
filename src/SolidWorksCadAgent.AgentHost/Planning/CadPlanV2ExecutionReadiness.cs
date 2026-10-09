using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    /// <summary>
    /// Review-only whole-plan check against the executor's actual registrations.
    /// A ready result is not authorization to approve or execute a v2 job.
    /// </summary>
    public static class CadPlanV2ExecutionReadiness
    {
        public static CadPlanV2ExecutionReadinessResult Assess(
            string persistedPlanJson, ExecutionMode mode, ICadCommandExecutor executor,
            Guid executionId, Guid managedModelId)
        {
            var errors = new List<string>();
            var parsed = CadPlanDocumentReader.ReadPersisted(persistedPlanJson);
            if (!parsed.IsValid || parsed.Version != 2 || parsed.CandidateV2 == null)
                errors.Add("INVALID_PLAN: A valid Host-normalized version-2 revision is required: " +
                    string.Join(" ", parsed.Errors));
            if (mode != ExecutionMode.Real)
                errors.Add("UNSUPPORTED_EXECUTION_MODE: Version-2 feature execution is unsupported in simulation.");
            if (executionId == Guid.Empty || managedModelId == Guid.Empty)
                errors.Add("INVALID_EXECUTION_CONTEXT: Host execution and managed-model IDs are required.");
            var availability = executor as ICadOperationAvailability;
            if (availability == null)
                errors.Add("EXECUTOR_CAPABILITIES_UNAVAILABLE: The active executor cannot report registered operations.");
            else if (!availability.SupportsManagedReferences)
                errors.Add("MODEL_REFERENCE_STORE_UNAVAILABLE: The active executor cannot consume managed references.");
            if (!(executor is IVersionedCadCommandExecutor))
                errors.Add("VERSIONED_DISPATCH_UNAVAILABLE: The active executor cannot dispatch versioned operations.");
            if (errors.Count > 0) return CadPlanV2ExecutionReadinessResult.Fail(errors);

            var requests = new List<CadVersionedCommandRequest>();
            var steps = parsed.CandidateV2.Steps;
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var version = step.OperationVersion.Value;
                if (step.Command == CadCommandNames.SavePart)
                {
                    if (index != 5 || steps.Count != 6 || version != 1 ||
                        steps[0].Command != CadCommandNames.NewPart ||
                        steps[1].Command != CadCommandNames.CreateSketch ||
                        steps[2].Command != CadCommandNames.AddRectangle ||
                        steps[3].Command != CadCommandNames.ExitSketch ||
                        steps[4].Command != CadCommandNames.Extrude ||
                        steps[4].OperationVersion != 2 ||
                        steps[1].Parameters?.Value<string>("plane") != "Top Plane" ||
                        steps[2].Parameters?.Value<double>("centerXmm") != 0.0 ||
                        steps[2].Parameters?.Value<double>("centerYmm") != 0.0 ||
                        string.IsNullOrWhiteSpace(step.Parameters?.Value<string>("path")) ||
                        step.Parameters.Value<bool?>("allowOverwrite") != false)
                    {
                        errors.Add("STEP_" + step.StepKey + ": SavePart preview supports only the final non-overwriting save of the first rectangle-boss plan.");
                        continue;
                    }
                    if (!availability.IsOperationRegistered(step.Command, version))
                    {
                        errors.Add("STEP_" + step.StepKey + ": No handler is registered for SavePart operation version 1.");
                        continue;
                    }
                    requests.Add(new CadVersionedCommandRequest(2, 1, new CadCommandEnvelope
                    {
                        Command = step.Command, Parameters = (JObject)step.Parameters.DeepClone(),
                        ExecutionId = executionId, ManagedModelId = managedModelId
                    }));
                    continue;
                }
                if (!availability.IsOperationRegistered(step.Command, version))
                {
                    errors.Add("STEP_" + step.StepKey + ": No handler is registered for " + step.Command +
                        " operation version " + version + ".");
                    continue;
                }

                if (version == 2 && step.Command == CadCommandNames.Extrude)
                {
                    try
                    {
                        requests.Add(CadPlanV2ExecutionBinder.BindExtrude(
                            persistedPlanJson, step.StepKey, executionId, managedModelId));
                    }
                    catch (ArgumentException ex)
                    {
                        errors.Add("STEP_" + step.StepKey + ": Host reference binding failed: " + ex.Message);
                    }
                    continue;
                }

                if (version != 1)
                {
                    errors.Add("STEP_" + step.StepKey + ": No Host binder exists for " + step.Command +
                        " operation version " + version + ".");
                    continue;
                }

                Guid? targetSketchId = null;
                if (step.Command == CadCommandNames.AddRectangle)
                {
                    if (index != 2 || steps[1].Command != CadCommandNames.CreateSketch ||
                        !steps[1].OutputEntityId.HasValue)
                    {
                        errors.Add("STEP_" + step.StepKey + ": The Host can bind only a third-step rectangle to the preceding sketch.");
                        continue;
                    }
                    targetSketchId = steps[1].OutputEntityId;
                }
                if (step.Command == CadCommandNames.ExitSketch)
                {
                    if (index != 3 || steps[1].Command != CadCommandNames.CreateSketch ||
                        !steps[1].OutputEntityId.HasValue || steps[2].Command != CadCommandNames.AddRectangle)
                    {
                        errors.Add("STEP_" + step.StepKey + ": The Host can bind only a fourth-step ExitSketch to the rectangle's sketch.");
                        continue;
                    }
                    targetSketchId = steps[1].OutputEntityId;
                }
                requests.Add(new CadVersionedCommandRequest(2, 1, new CadCommandEnvelope
                {
                    Command = step.Command,
                    Parameters = (JObject)step.Parameters.DeepClone(),
                    ExecutionId = executionId,
                    ManagedModelId = managedModelId,
                    OutputEntityId = step.Command == CadCommandNames.CreateSketch ? step.OutputEntityId : null
                }, targetSketchEntityId: targetSketchId));
            }

            return errors.Count == 0
                ? CadPlanV2ExecutionReadinessResult.Ready(requests)
                : CadPlanV2ExecutionReadinessResult.Fail(errors);
        }
    }

    public sealed class CadPlanV2ExecutionReadinessResult
    {
        private CadPlanV2ExecutionReadinessResult(
            IReadOnlyList<string> errors, IReadOnlyList<CadVersionedCommandRequest> requests)
        {
            Errors = errors;
            Requests = requests;
        }

        public bool IsReady => Errors.Count == 0;
        public IReadOnlyList<string> Errors { get; }
        public IReadOnlyList<CadVersionedCommandRequest> Requests { get; }

        internal static CadPlanV2ExecutionReadinessResult Ready(IEnumerable<CadVersionedCommandRequest> requests) =>
            new CadPlanV2ExecutionReadinessResult(Array.Empty<string>(),
                new ReadOnlyCollection<CadVersionedCommandRequest>(requests.ToList()));

        internal static CadPlanV2ExecutionReadinessResult Fail(IEnumerable<string> errors) =>
            new CadPlanV2ExecutionReadinessResult(new ReadOnlyCollection<string>(errors.ToList()),
                Array.Empty<CadVersionedCommandRequest>());
    }
}
