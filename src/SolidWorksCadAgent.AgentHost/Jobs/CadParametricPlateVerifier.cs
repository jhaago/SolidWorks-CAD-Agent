using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    public sealed class CadParametricPlateVerifier
    {
        private readonly SqliteJobRepository _repository;
        private readonly ICadCommandExecutor _executor;
        public CadParametricPlateVerifier(SqliteJobRepository repository,ICadCommandExecutor executor)
        {_repository=repository??throw new ArgumentNullException(nameof(repository));_executor=executor??throw new ArgumentNullException(nameof(executor));}
        public static IReadOnlyList<string> Check(CadParametricPlateIntent intent,CadParametricPlateObservation observed)
        {
            var errors=new List<string>();
            if(intent==null||observed==null||observed.EvidenceVersion!=1||observed.ModelId==Guid.Empty||observed.SketchEntityId==Guid.Empty||string.IsNullOrWhiteSpace(observed.ConfigurationKey))
            {errors.Add("Native parametric ownership observation missing.");return errors;}
            if(!observed.FullyDefined||!observed.RequiredRelationsPresent||observed.HasFixedContourEntities||observed.ContourEdgeCount!=4||observed.ConstructionEdgeCount!=1||observed.SolidBodyCount!=1||observed.RebuildErrorCount!=0)
                errors.Add("Native sketch constraints, contour or rebuild/body state differ from plate intent.");
            var roles=new[]{"width","length","thickness"};
            if(observed.Dimensions==null||observed.Dimensions.Count!=3||observed.Dimensions.Any(d=>d==null)||observed.Dimensions.Select(d=>d.Role).Distinct().Count()!=3)
            {errors.Add("Exactly three distinct native driving dimensions required.");return errors;}
            foreach(var dimension in observed.Dimensions)
            {
                if(!roles.Contains(dimension.Role)){errors.Add("Unexpected native dimension role.");continue;}
                double expected=dimension.Role=="width"?intent.WidthMm:dimension.Role=="length"?intent.LengthMm:intent.ThicknessMm;
                if(!dimension.IsDriving||double.IsNaN(expected)||double.IsInfinity(expected)||expected<=0||double.IsNaN(dimension.ValueMm)||double.IsInfinity(dimension.ValueMm)||
                    Math.Abs(expected-dimension.ValueMm)>0.001||dimension.EntityId!=CadParametricIdentity.For(observed.ModelId,observed.SketchEntityId,dimension.Role)||
                    dimension.OwnerEntityId!=(dimension.Role=="thickness"?CadParametricIdentity.For(observed.ModelId,observed.SketchEntityId,"boss"):observed.SketchEntityId))
                    errors.Add("Native driving parameter value or owner differs: "+dimension.Role);
            }
            return errors;
        }
        public async Task<CadV2RectangleBossVerification> VerifyAsync(Guid jobId,Guid revisionId,Guid modelId,CancellationToken token)
        {
            var snapshot=await _repository.GetSnapshotAsync(jobId,token).ConfigureAwait(false);
            var revision=snapshot?.Revisions.LastOrDefault();
            if(revision==null||revision.Id!=revisionId)throw new InvalidOperationException("Current approved revision required.");
            var parsed=CadPlanDocumentReader.ReadPersisted(revision.PlanJson);
            var intent=CadParametricPlateContract.ReadIntent(parsed.CandidateV2);
            var sketch=parsed.CandidateV2.Steps[1].OutputEntityId.Value;
            var result=await _executor.ExecuteAsync(new CadCommandEnvelope {Command="InspectParametricPlate",ExecutionId=jobId,ManagedModelId=modelId,
                Parameters=new JObject { ["sketchEntityId"]=sketch.ToString("D") }},token).ConfigureAwait(false);
            if(result==null||!result.Success||result.Error!=null||result.Data?["parametricPlate"]==null)return CadV2RectangleBossVerification.Fail("Native parameter readback failed: "+(result?.Error?.Message??"Missing observation."));
            CadParametricPlateObservation observation;
            try{observation=result.Data["parametricPlate"].ToObject<CadParametricPlateObservation>();}
            catch(Exception ex){return CadV2RectangleBossVerification.Fail("Invalid native parameter readback: "+ex.Message);}
            if(observation==null||observation.ModelId!=modelId||observation.SketchEntityId!=sketch)return CadV2RectangleBossVerification.Fail("Native parameter ownership differs.");
            var model=await _repository.GetModelAsync(modelId,token).ConfigureAwait(false);
            if(model==null||model.ConfigurationKey!=observation.ConfigurationKey)return CadV2RectangleBossVerification.Fail("Native parameter configuration differs.");
            var errors=Check(intent,observation);
            return errors.Count==0?CadV2RectangleBossVerification.Pass(result.Data):CadV2RectangleBossVerification.Fail(string.Join(" ",errors));
        }
    }
}
