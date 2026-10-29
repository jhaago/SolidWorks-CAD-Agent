#if SOLIDWORKS_INTEROP
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorksCadAgent.Contracts.Cad;

namespace SolidWorksCadAgent.SolidWorksBridge.Inspection
{
    /// <summary>Only called within the existing bound-document STA operation.</summary>
    internal static class NativeFeatureMaterialVerifier
    {
        internal static CadCommandResult Execute(ModelDoc2 model, MaterialChangeKind kind, Func<Feature> create,
            Func<Feature, object> resultData)
        {
            try
            {
                CadCommandResult result = null;
                FeatureMaterialOperation.Run(kind, () => Measure(model), create, feature =>
                {
                    string type = feature.GetTypeName2();
                    if (type == "ICE") type = feature.GetTypeName();
                    bool expected = kind == MaterialChangeKind.Add ? type == "Boss" || type == "BossExtrude"
                        : type == "Cut" || type == "CutExtrude";
                    if (!expected) throw new InvalidOperationException("SOLIDWORKS returned an unexpected native feature type: " + type);
                    var health = SolidWorksInspector.RebuildAndInspect(model);
                    if (health.HasErrors) throw new InvalidOperationException("The model has native rebuild errors after feature creation.");
                }, (feature, evidence) =>
                {
                    result = CadCommandResult.Ok(resultData(feature));
                    // Use the existing camel-case serializer; do not expose the COM object.
                    result.Data["materialVerification"] = CadCommandResult.Ok(evidence).Data;
                });
                return result;
            }
            catch (FeatureMaterialVerificationException error)
            {
                var result = CadCommandResult.Ok(new
                {
                    mutationAttempted = error.MutationAttempted,
                    documentMayBeModified = error.MutationAttempted,
                    rollbackAttempted = false,
                    before = error.Before,
                    after = error.After
                });
                result.Success = false;
                result.Error = new CadError
                {
                    Code = error.MutationAttempted ? "FEATURE_MATERIAL_VERIFICATION_FAILED" : "FEATURE_MATERIAL_PREFLIGHT_FAILED",
                    Stage = "Verify",
                    Message = error.MutationAttempted
                        ? "Feature execution or verification failed. The document may contain partial changes; no rollback was attempted."
                        : "Native material could not be measured. Feature execution was not attempted.",
                    Detail = error.Message
                };
                return result;
            }
        }

        internal static SolidMaterialSnapshot Measure(ModelDoc2 model) =>
            SolidMaterialSnapshot.FromBodyMassProperties(SolidWorksInspector.GetSolidBodies(model).Select(bodyObject =>
            {
                var body = bodyObject as Body2;
                if (body == null) throw new InvalidOperationException("Native solid body is unavailable.");
                return body.GetMassProperties(1.0) as double[]; // SI units; includes hidden solids.
            }));
    }
}
#endif
