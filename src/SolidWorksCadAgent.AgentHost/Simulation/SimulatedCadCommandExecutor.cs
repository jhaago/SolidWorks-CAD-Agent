using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.AgentHost.Simulation
{
    public sealed class SimulatedCadCommandExecutor : ICadCommandExecutor, IVersionedCadCommandExecutor
    {
        private readonly List<CadCommandEnvelope> _executedCommands = new List<CadCommandEnvelope>();
        private readonly List<string> _features = new List<string>();
        private bool _hasPart;
        private bool _sketchOpen;
        private string _pendingSketch;
        private string _completedSketch;
        private double _widthMm;
        private double _heightMm;
        private double _depthMm;
        private double _holeDiameterMm;
        private bool _hasBody;

        public IReadOnlyList<CadCommandEnvelope> ExecutedCommands =>
            new ReadOnlyCollection<CadCommandEnvelope>(_executedCommands);

        public Task<CadCommandResult> ExecuteAsync(
            CadCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command == null || string.IsNullOrWhiteSpace(command.Command))
                return Task.FromResult(Failure("INVALID_COMMAND", "A CAD command name is required."));

            var parameters = command.Parameters ?? new JObject();
            _executedCommands.Add(new CadCommandEnvelope
            {
                Command = command.Command,
                Parameters = (JObject)parameters.DeepClone()
            });

            CadCommandResult result;
            switch (command.Command)
            {
                case CadCommandNames.NewPart:
                    result = NewPart();
                    break;
                case CadCommandNames.CreateSketch:
                    result = CreateSketch(parameters);
                    break;
                case CadCommandNames.AddLine:
                case CadCommandNames.AddArc:
                    result = AddCustomPrimitive(command.Command, parameters);
                    break;
                case CadCommandNames.AddSlot:
                case CadCommandNames.AddRegularPolygon:
                    result = AddPrismaticProfile(command.Command, parameters);
                    break;
                case CadCommandNames.AddRectangle:
                    result = AddRectangle(parameters);
                    break;
                case CadCommandNames.AddCircle:
                    result = AddCircle(parameters);
                    break;
                case CadCommandNames.ExitSketch:
                    result = ExitSketch();
                    break;
                case CadCommandNames.Extrude:
                    result = Extrude(parameters);
                    break;
                case CadCommandNames.CutExtrude:
                    result = CutExtrude(parameters);
                    break;
                case CadCommandNames.Rebuild:
                    result = RequirePart(() => CadCommandResult.Ok(new { rebuilt = true }));
                    break;
                case CadCommandNames.GetBodyCount:
                    result = RequirePart(() => CadCommandResult.Ok(new { bodyCount = _hasBody ? 1 : 0 }));
                    break;
                case CadCommandNames.GetBoundingBox:
                    result = _hasBody
                        ? CadCommandResult.Ok(new { sizeXmm = _widthMm, sizeYmm = _heightMm, sizeZmm = _depthMm })
                        : Failure("NO_SOLID_BODY", "The simulated part has no solid body to inspect.");
                    break;
                case CadCommandNames.GetFeatureTree:
                    result = RequirePart(() => CadCommandResult.Ok(new { features = _features.ToArray() }));
                    break;
                case CadCommandNames.GetRebuildErrors:
                    result = RequirePart(() => CadCommandResult.Ok(new { hasErrors = false, errors = new string[0] }));
                    break;
                case CadCommandNames.SavePart:
                    result = SavePart(parameters);
                    break;
                case CadCommandNames.CloseDocument:
                    result = CloseDocument();
                    break;
                default:
                    result = Failure("UNSUPPORTED_COMMAND", "CAD command is not registered: " + command.Command);
                    break;
            }

            return Task.FromResult(result);
        }

        public Task<CadCommandResult> ExecuteVersionedAsync(
            CadVersionedCommandRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null)
                return Task.FromResult(Failure("INVALID_EXECUTION_REQUEST", "A versioned CAD execution request is required."));
            if (request.PlanVersion != 1)
                return Task.FromResult(Failure("VERSIONED_PLAN_UNSUPPORTED_IN_SIMULATION", "Simulation cannot execute a versioned CAD plan or verify its referenced feature geometry."));
            if (request.OperationVersion != 1 || request.ProfileSketchEntityId.HasValue)
                return Task.FromResult(Failure("UNSUPPORTED_OPERATION_VERSION", "Simulation supports only the legacy version-1 command contract."));
            return ExecuteAsync(request.Command, cancellationToken);
        }

        private CadCommandResult NewPart()
        {
            _hasPart = true;
            _sketchOpen = false;
            _pendingSketch = null;
            _completedSketch = null;
            _widthMm = 0;
            _heightMm = 0;
            _depthMm = 0;
            _holeDiameterMm = 0;
            _hasBody = false;
            _features.Clear();
            return CadCommandResult.Ok(new { documentTitle = "SimulatedPart1" });
        }

        private CadCommandResult CreateSketch(JObject parameters)
        {
            if (!_hasPart) return Failure("NO_ACTIVE_DOCUMENT", "Create a part before creating a sketch.");
            if (_sketchOpen) return Failure("SKETCH_ALREADY_OPEN", "Exit the active sketch before creating another sketch.");
            var plane = (string)parameters["plane"];
            if (plane != "Top Plane" && plane != "Front Plane" && plane != "Right Plane")
                return Failure("UNSUPPORTED_PARAMETER_VALUE", "The requested sketch plane is not supported.");
            _sketchOpen = true;
            _pendingSketch = null;
            return CadCommandResult.Ok(new { plane });
        }

        private CadCommandResult AddCustomPrimitive(string name, JObject p)
        {
            var error = name == CadCommandNames.AddLine ? SketchPrimitiveValidation.ValidateLine(p) : SketchPrimitiveValidation.ValidateArc(p);
            if (error != null) return Failure("INVALID_PARAMETERS", error);
            if (!_sketchOpen) return Failure("NO_ACTIVE_SKETCH", "Create a sketch before adding custom geometry.");
            _pendingSketch = "Custom";
            return CadCommandResult.Ok(new { created = true, isSimulated = true });
        }
        private CadCommandResult AddPrismaticProfile(string name, JObject parameters)
        {
            var error = name == CadCommandNames.AddSlot ? PrismaticProfileGeometry.ValidateSlot(parameters)
                : PrismaticProfileGeometry.ValidateRegularPolygon(parameters);
            if (error != null) return Failure("INVALID_PARAMETERS", error);
            if (!_sketchOpen) return Failure("NO_ACTIVE_SKETCH", "Create a sketch before adding profile geometry.");
            _pendingSketch = "Custom";
            return CadCommandResult.Ok(new { created = true, isSimulated = true });
        }

        private CadCommandResult AddRectangle(JObject parameters)
        {
            if (!_sketchOpen) return Failure("NO_ACTIVE_SKETCH", "Create a sketch before adding a rectangle.");
            if (!TryPositive(parameters, "widthMm", out var width) || !TryPositive(parameters, "heightMm", out var height))
                return Failure("INVALID_PARAMETERS", "Rectangle width and height must be positive millimetre values.");
            _widthMm = width;
            _heightMm = height;
            if (_pendingSketch != "Custom") _pendingSketch = "Rectangle";
            return CadCommandResult.Ok(new { created = true });
        }

        private CadCommandResult AddCircle(JObject parameters)
        {
            if (!_sketchOpen) return Failure("NO_ACTIVE_SKETCH", "Create a sketch before adding a circle.");
            if (!TryPositive(parameters, "diameterMm", out var diameter))
                return Failure("INVALID_PARAMETERS", "Circle diameter must be a positive millimetre value.");
            _holeDiameterMm = diameter;
            if (_pendingSketch != "Custom") _pendingSketch = "Circle";
            return CadCommandResult.Ok(new { created = true });
        }

        private CadCommandResult ExitSketch()
        {
            if (!_sketchOpen) return Failure("NO_ACTIVE_SKETCH", "There is no active sketch to exit.");
            if (_pendingSketch == null) return Failure("EMPTY_SKETCH", "The simulated sketch contains no supported geometry.");
            _sketchOpen = false;
            _completedSketch = _pendingSketch;
            _pendingSketch = null;
            return CadCommandResult.Ok(new { exited = true });
        }

        private CadCommandResult Extrude(JObject parameters)
        {
            var contractError = CadPlanningCommandContract.Validate(new CadCommandEnvelope
            {
                Command = CadCommandNames.Extrude,
                Parameters = parameters
            });
            if (contractError != null) return Failure("INVALID_PARAMETERS", contractError);
            if (_completedSketch == "Custom") return Failure("SIMULATION_PROFILE_UNSUPPORTED", "Custom line/arc profile solids require real SOLIDWORKS execution; this simulator does not verify their geometry.");
            if (_completedSketch != "Rectangle") return Failure("INVALID_SKETCH_PROFILE", "Extrude requires a completed rectangle sketch.");
            if (!TryPositive(parameters, "depthMm", out var depth))
                return Failure("INVALID_PARAMETERS", "Extrude depth must be a positive millimetre value.");
            _depthMm = depth;
            _hasBody = true;
            _completedSketch = null;
            _features.Add("BossExtrude");
            return CadCommandResult.Ok(new { created = true });
        }

        private CadCommandResult CutExtrude(JObject parameters)
        {
            var error = PrismaticProfileGeometry.ValidateCut(parameters);
            if (error != null) return Failure("INVALID_PARAMETERS", error);
            if (!_hasBody) return Failure("NO_SOLID_BODY", "Create a solid body before cutting it.");
            if (_completedSketch == "Custom")
                return Failure("SIMULATION_PROFILE_UNSUPPORTED", "Custom profile cuts require real SOLIDWORKS execution; this simulator does not verify their geometry.");
            if ((string)parameters["endCondition"] == "Blind")
                return Failure("SIMULATION_FEATURE_UNSUPPORTED", "Blind pocket geometry requires real SOLIDWORKS execution; this simulator does not verify cut depth.");
            if (_completedSketch != "Circle" || _holeDiameterMm <= 0)
                return Failure("INVALID_SKETCH_PROFILE", "CutExtrude requires a completed circle sketch.");
            if ((string)parameters["endCondition"] != "ThroughAll")
                return Failure("UNSUPPORTED_PARAMETER_VALUE", "Only ThroughAll cuts are supported.");
            _completedSketch = null;
            _features.Add("CutExtrude");
            return CadCommandResult.Ok(new { created = true });
        }

        private CadCommandResult SavePart(JObject parameters)
        {
            return RequirePart(() =>
            {
                var path = (string)parameters["path"];
                if (!string.IsNullOrWhiteSpace(path) &&
                    path.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase))
                {
                    return Failure(
                        "SIMULATION_NATIVE_FORMAT_NOT_ALLOWED",
                        "Simulation cannot create a native SOLIDWORKS .SLDPRT file.");
                }

                return CadCommandResult.Ok(new { saved = true, path });
            });
        }

        private CadCommandResult CloseDocument()
        {
            if (!_hasPart) return Failure("NO_ACTIVE_DOCUMENT", "There is no simulated document to close.");
            _hasPart = false;
            _sketchOpen = false;
            return CadCommandResult.Ok(new { closed = true });
        }

        private CadCommandResult RequirePart(Func<CadCommandResult> action)
        {
            return _hasPart ? action() : Failure("NO_ACTIVE_DOCUMENT", "There is no active simulated part document.");
        }

        private static bool TryPositive(JObject parameters, string name, out double value)
        {
            value = 0;
            var token = parameters[name];
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)) return false;
            value = token.Value<double>();
            return value > 0 && !double.IsInfinity(value) && !double.IsNaN(value);
        }

        private static CadCommandResult Failure(string code, string message)
        {
            return new CadCommandResult
            {
                Success = false,
                Data = new JObject(),
                Error = new CadError
                {
                    Code = code,
                    Stage = "Simulation",
                    Message = message
                }
            };
        }
    }
}
