using System;
using Newtonsoft.Json;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.Core.Commands
{
    /// <summary>
    /// Host-created execution context for a step from a versioned persisted plan.
    /// This type is not a planner or persistence DTO; trusted identity values must
    /// come from the normalized approved revision and Host identity registry.
    /// </summary>
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class CadVersionedCommandRequest
    {
        public CadVersionedCommandRequest(
            int planVersion,
            int operationVersion,
            CadCommandEnvelope command,
            Guid? profileSketchEntityId = null)
        {
            PlanVersion = planVersion;
            OperationVersion = operationVersion;
            Command = command;
            ProfileSketchEntityId = profileSketchEntityId;
        }

        public int PlanVersion { get; }
        public int OperationVersion { get; }
        public CadCommandEnvelope Command { get; }
        public Guid? ProfileSketchEntityId { get; }
    }
}
