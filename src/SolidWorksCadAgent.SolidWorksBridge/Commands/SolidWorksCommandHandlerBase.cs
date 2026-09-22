using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.SolidWorksBridge.Commands
{
    public abstract class SolidWorksCommandHandlerBase : ICadCommandHandler
    {
        protected SolidWorksCommandHandlerBase(ISolidWorksSession session)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
        }

        protected ISolidWorksSession Session { get; }

        public abstract string Name { get; }

        public abstract CadError Validate(JObject parameters);

        public abstract Task<CadCommandResult> ExecuteAsync(
            JObject parameters,
            CancellationToken cancellationToken);

        protected Task<CadCommandResult> InvokeAsync(
            Func<object, CadCommandResult> operation,
            CancellationToken cancellationToken)
        {
            return Session.InvokeWithApplicationAsync(operation, cancellationToken);
        }

        protected static CadCommandResult Ok(object data = null)
        {
            return CadCommandResult.Ok(data);
        }

        protected static CadCommandResult Failure(
            string code,
            string stage,
            string message,
            string detail = null)
        {
            return new CadCommandResult
            {
                Success = false,
                Data = new JObject(),
                Error = new CadError
                {
                    Code = code,
                    Stage = stage,
                    Message = message,
                    Detail = detail
                }
            };
        }

        protected static CadError InvalidParameter(string message)
        {
            return new CadError
            {
                Code = "INVALID_PARAMETERS",
                Stage = "Validate",
                Message = message
            };
        }

        protected static CadError UnsupportedValue(string message)
        {
            return new CadError
            {
                Code = "UNSUPPORTED_PARAMETER_VALUE",
                Stage = "Validate",
                Message = message
            };
        }

        protected static bool TryGetFiniteDouble(
            JObject parameters,
            string name,
            out double value)
        {
            value = 0.0;
            if (parameters == null || parameters[name] == null)
            {
                return false;
            }

            try
            {
                value = parameters[name].Value<double>();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch
            {
                return false;
            }
        }

        protected static string GetString(JObject parameters, string name)
        {
            return parameters?[name]?.Type == JTokenType.String
                ? parameters[name].Value<string>()
                : null;
        }

        protected static CadCommandResult InteropUnavailable()
        {
            return Failure(
                "SOLIDWORKS_INTEROP_UNAVAILABLE",
                "Execute",
                "This build was compiled without the installed SOLIDWORKS interop libraries.");
        }
    }
}
