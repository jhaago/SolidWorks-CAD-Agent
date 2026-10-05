using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.AgentHost.Planning
{
    public sealed class DeterministicCadPlanningProvider : ICadPlanningProvider
    {
        public Task<CadPlanningResult> PlanAsync(
            CadPlanningRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("A CAD prompt is required.", nameof(request));

            var prompt = request.Prompt;
            var clarifications = request.Clarifications == null
                ? string.Empty
                : string.Join(" ", request.Clarifications);
            if (prompt.IndexOf("M8", StringComparison.OrdinalIgnoreCase) >= 0 &&
                !Contains(prompt + " " + clarifications, "tapped") &&
                !Contains(prompt + " " + clarifications, "clearance"))
            {
                return Task.FromResult(Clarification(
                    "Specify whether the M8 hole is tapped or clearance, including the required fit if clearance."));
            }

            if (Contains(prompt, "M8") && Contains(clarifications, "clearance") &&
                (Contains(clarifications, "9 mm") || Contains(clarifications, "9mm")) &&
                Contains(prompt, "100") && Contains(prompt, "60") && Contains(prompt, "10"))
            {
                return Task.FromResult(PlateWithHole(9.0, "M8 clearance"));
            }

            if (IsAcceptancePlate(prompt))
            {
                return Task.FromResult(PlateWithHole(20.0, "Ø20"));
            }

            return Task.FromResult(Clarification(
                "The deterministic planner only recognises the V1 acceptance plate. Provide explicit dimensions and feature intent or use a configured cloud planner."));
        }

        private static CadPlanningResult PlateWithHole(double diameterMm, string holeDescription)
        {
            return new CadPlanningResult
            {
                Provider = "deterministic",
                Model = "v1-rules",
                Summary = "Create a native 100 × 60 × 10 mm rectangular plate with a centred " + holeDescription + " through-hole.",
                ProposedCommands = new List<CadCommandEnvelope>
                {
                    Command(CadCommandNames.NewPart),
                    Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                    Command(CadCommandNames.AddRectangle, new { centerXmm = 0.0, centerYmm = 0.0, widthMm = 100.0, heightMm = 60.0 }),
                    Command(CadCommandNames.ExitSketch),
                    Command(CadCommandNames.Extrude, new { depthMm = 10.0 }),
                    Command(CadCommandNames.CreateSketch, new { plane = "Top Plane" }),
                    Command(CadCommandNames.AddCircle, new { centerXmm = 0.0, centerYmm = 0.0, diameterMm }),
                    Command(CadCommandNames.ExitSketch),
                    Command(CadCommandNames.CutExtrude, new { endCondition = "ThroughAll" }),
                    Command(CadCommandNames.Rebuild)
                }
            };
        }

        private static CadPlanningResult Clarification(string ambiguity)
        {
            return new CadPlanningResult
            {
                Provider = "deterministic",
                Model = "v1-rules",
                Summary = "Clarification is required before a safe CAD plan can be produced.",
                Ambiguities = new List<string> { ambiguity }
            };
        }

        private static bool IsAcceptancePlate(string prompt)
        {
            return Contains(prompt, "100") &&
                   Contains(prompt, "60") &&
                   Contains(prompt, "10") &&
                   Contains(prompt, "20") &&
                   (Contains(prompt, "through-hole") || Contains(prompt, "through hole"));
        }

        private static bool Contains(string value, string part)
        {
            return value.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static CadCommandEnvelope Command(string name, object parameters = null)
        {
            return new CadCommandEnvelope
            {
                Command = name,
                Parameters = parameters == null ? new JObject() : JObject.FromObject(parameters)
            };
        }
    }
}
