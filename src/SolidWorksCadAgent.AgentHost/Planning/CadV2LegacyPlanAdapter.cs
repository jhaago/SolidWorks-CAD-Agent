using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Ai;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    /// <summary>Opt-in adaptation of existing intent planning to explicit V2 sketch dependencies.</summary>
    public static class CadV2LegacyPlanAdapter
    {
        public const string PlanningContext = "V2 execution scope: create a NEW part using Top Plane, Front Plane or Right Plane sketches. Each sketch contains exactly one rectangle, circle, slot or regular polygon, OR one ordered closed simple contour made only from AddLine and AddArc segments. " +
            "For a contour, each segment end must match the next segment start (including last to first), and segments must not cross or touch except at their shared endpoints. Do not mix contour segments with standalone primitives or create multiple loops. " +
            "Finish each profile with ExitSketch and Extrude. Optional cuts use separate sketches on the same plane, strictly inside a rectangular base, with disjoint profile bounding boxes; Blind depth must be less than boss depth. " +
            "Finish with one SavePart using a new relative filename and allowOverwrite:false. Final rebuild and close/reopen verification are handled by the Host; do not add CloseDocument. " +
            "Do not approximate requested unsupported geometry or guess missing dimensions. Ask for clarification instead.";
        public static bool IsRequest(string prompt) =>
            prompt != null && prompt.TrimStart().StartsWith("V2:", StringComparison.OrdinalIgnoreCase);
        public static string PlanningPrompt(string prompt) =>
            IsRequest(prompt) ? prompt.TrimStart().Substring(3).Trim() : prompt;

        public static string BuildCandidate(CadPlanningResult source)
        {
            if (source == null) throw new ArgumentException("The planner returned no result.", nameof(source));
            var plan = new CadPlanV2Document
            {
                PlanVersion = 2, Summary = source.Summary,
                Assumptions = new List<string>(source.Assumptions ?? new List<string>()),
                Ambiguities = new List<string>(source.Ambiguities ?? new List<string>()),
                Steps = new List<CadPlanV2Step>()
            };
            string sketch = null;
            var commands = source.ProposedCommands ?? new List<CadCommandEnvelope>();
            for (int i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                var error = CadPlanningCommandContract.Validate(command);
                if (error != null) throw new ArgumentException(error, nameof(source));
                if (command.ExecutionId.HasValue || command.ManagedModelId.HasValue || command.OutputEntityId.HasValue)
                    throw new ArgumentException("Planner commands cannot assign execution identities.", nameof(source));
                // The final rebuild is performed by mandatory pre-save and post-reopen observation.
                if (command.Command == CadCommandNames.Rebuild && i == commands.Count - 2 &&
                    commands[i + 1]?.Command == CadCommandNames.SavePart)
                {
                    plan.Assumptions.Add("The final rebuild is performed and checked by V2 save verification.");
                    continue;
                }
                var step = new CadPlanV2Step
                {
                    StepKey = "step-" + (i + 1), Command = command.Command, OperationVersion = 1,
                    Parameters = command.Parameters == null ? new JObject() : (JObject)command.Parameters.DeepClone()
                };
                if (command.Command == CadCommandNames.CreateSketch)
                {
                    sketch = "sketch-" + (i + 1);
                    step.OutputKey = sketch;
                }
                else if (command.Command == CadCommandNames.Extrude || command.Command == CadCommandNames.CutExtrude)
                {
                    step.OperationVersion = 2;
                    step.Inputs = new CadPlanV2Inputs { ProfileSketch = new CadPlanV2Reference { Kind = "Sketch", OutputKey = sketch } };
                    sketch = null;
                }
                else if (command.Command == CadCommandNames.SavePart && step.Parameters["allowOverwrite"] == null)
                    step.Parameters["allowOverwrite"] = false;
                plan.Steps.Add(step);
            }
            return JsonConvert.SerializeObject(plan);
        }
    }
}
