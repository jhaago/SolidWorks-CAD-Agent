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
    public sealed class CadParametricHoleVerifier
    {
        private readonly SqliteJobRepository _repository;
        private readonly ICadCommandExecutor _executor;
        public CadParametricHoleVerifier(SqliteJobRepository repository,ICadCommandExecutor executor)
        {_repository=repository??throw new ArgumentNullException(nameof(repository));_executor=executor??throw new ArgumentNullException(nameof(executor));}
        public static IReadOnlyList<string> Check(CadParametricHoleIntent intent,CadParametricHoleObservation observed)
        {
            var errors=new List<string>();
            if(intent==null||observed==null||observed.EvidenceVersion!=1||observed.ModelId==Guid.Empty||observed.SketchEntityId==Guid.Empty||string.IsNullOrWhiteSpace(observed.ConfigurationKey))
            {errors.Add("Native hole ownership observation missing.");return errors;}
            if(!observed.FullyDefined||!observed.RequiredRelationsPresent||observed.HasFixedContourEntities||observed.ContourEdgeCount!=1||observed.ConstructionEdgeCount!=0||observed.SolidBodyCount!=1||observed.RebuildErrorCount!=0)
                errors.Add("Native hole constraints, contour or rebuild/body state differ from intent.");
            var roles=new[]{"holeDiameter","holeX","holeY"};
            if(observed.Dimensions==null||observed.Dimensions.Count!=3||observed.Dimensions.Any(d=>d==null)||observed.Dimensions.Select(d=>d.Role).Distinct().Count()!=3)
                errors.Add("Exactly three distinct native hole driving dimensions required.");
            else foreach(var d in observed.Dimensions)
            {
                if(!roles.Contains(d.Role)){errors.Add("Unexpected native hole dimension role.");continue;}
                var expected=d.Role=="holeDiameter"?intent.DiameterMm:d.Role=="holeX"?intent.Xmm:intent.Ymm;
                if(!d.IsDriving||!Matches(expected,d.ValueMm)||d.EntityId!=CadParametricIdentity.For(observed.ModelId,observed.SketchEntityId,d.Role)||d.OwnerEntityId!=observed.SketchEntityId)
                    errors.Add("Native hole driving value or owner differs: "+d.Role);
            }
            if(!MatchesGeometry(observed.Geometry,"centerXmm",intent.Xmm)||!MatchesGeometry(observed.Geometry,"centerYmm",intent.Ymm)||!MatchesGeometry(observed.Geometry,"diameterMm",intent.DiameterMm))
                errors.Add("Native circle geometry differs from hole intent.");
            return errors;
        }
        private static bool Matches(double expected,double actual)=>!double.IsNaN(expected)&&!double.IsInfinity(expected)&&expected>0&&
            !double.IsNaN(actual)&&!double.IsInfinity(actual)&&Math.Abs(expected-actual)<=0.001;
        private static bool MatchesGeometry(JObject geometry,string key,double expected)
        {
            var value=geometry?[key];
            return value!=null&&(value.Type==JTokenType.Float||value.Type==JTokenType.Integer)&&Matches(expected,value.Value<double>());
        }
        public async Task<CadV2RectangleBossVerification> VerifyAsync(Guid jobId,Guid revisionId,Guid modelId,CancellationToken token)
        {
            var snapshot=await _repository.GetSnapshotAsync(jobId,token).ConfigureAwait(false);var revision=snapshot?.Revisions.LastOrDefault();
            if(revision==null||revision.Id!=revisionId)throw new InvalidOperationException("Current approved revision required.");
            var parsed=CadPlanDocumentReader.ReadPersisted(revision.PlanJson);var intent=CadParametricHoleContract.ReadIntent(parsed.CandidateV2);
            var sketch=parsed.CandidateV2.Steps[5].OutputEntityId.Value;
            var result=await _executor.ExecuteAsync(new CadCommandEnvelope{Command="InspectParametricHole",ExecutionId=jobId,ManagedModelId=modelId,
                Parameters=new JObject{["sketchEntityId"]=sketch.ToString("D")}},token).ConfigureAwait(false);
            if(result==null||!result.Success||result.Error!=null||result.Data?["parametricHole"]==null)
                return CadV2RectangleBossVerification.Fail("Native hole readback failed: "+(result?.Error?.Message??"Missing observation."));
            CadParametricHoleObservation observation;
            try{observation=result.Data["parametricHole"].ToObject<CadParametricHoleObservation>();}
            catch(Exception ex){return CadV2RectangleBossVerification.Fail("Invalid native hole readback: "+ex.Message);}
            if(observation==null||observation.ModelId!=modelId||observation.SketchEntityId!=sketch)return CadV2RectangleBossVerification.Fail("Native hole ownership differs.");
            var model=await _repository.GetModelAsync(modelId,token).ConfigureAwait(false);
            if(model==null||model.ConfigurationKey!=observation.ConfigurationKey)return CadV2RectangleBossVerification.Fail("Native hole configuration differs.");
            var errors=Check(intent,observation);
            return errors.Count==0?CadV2RectangleBossVerification.Pass(result.Data):CadV2RectangleBossVerification.Fail(string.Join(" ",errors));
        }
    }
}
