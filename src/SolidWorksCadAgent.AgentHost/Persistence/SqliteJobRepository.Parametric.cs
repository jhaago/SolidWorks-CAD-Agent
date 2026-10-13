using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed partial class SqliteJobRepository
    {
        public Task CommitV2ParametricOutcomeAsync(Guid jobId,Guid revisionId,string stepKey,Guid modelId,CadCommandResult result,CancellationToken token)
        {
            ThrowIfDisposed();token.ThrowIfCancellationRequested();
            using(var connection=OpenConnection())
            using(var transaction=connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var context=ReadV2SequentialContext(connection,transaction,jobId,revisionId,stepKey,modelId);
                RequireV2SequentialPrepared(context);
                bool rectangle=context.Step.Command=="AddRectangle"&&context.Step.OperationVersion==2;
                bool boss=context.Step.Command=="Extrude"&&context.Step.OperationVersion==3;
                if((!rectangle&&!boss)||!MatchesV2SequentialOutcome(context.Step,result))throw new InvalidOperationException("Parametric native result does not match its reserved step.");
                var plan=context.Plan;
                var intent=CadParametricPlateContract.ReadIntent(plan);var sketch=plan.Steps[1].OutputEntityId.Value;
                CadParametricPlateObservation captured;
                try{captured=result.Data["parametricCapture"]?.ToObject<CadParametricPlateObservation>();}
                catch(Exception ex){throw new InvalidOperationException("Invalid parametric capture.",ex);}
                string[] roles=rectangle?new[]{"bottom","right","top","left","centreDiagonal","width","length"}:new[]{"boss","thickness"};
                string[] dimensions=rectangle?new[]{"width","length"}:new[]{"thickness"};
                if(captured==null||captured.EvidenceVersion!=1||captured.ModelId!=modelId||captured.SketchEntityId!=sketch||
                    captured.ConfigurationKey!=context.Model.ConfigurationKey||captured.Roles==null||captured.Dimensions==null||
                    captured.Roles.Count!=roles.Length||captured.Roles.Select(r=>r?.Role).Distinct().Count()!=roles.Length||
                    captured.Roles.Any(r=>r==null||!roles.Contains(r.Role)||r.EntityId!=CadParametricIdentity.For(modelId,sketch,r.Role)||r.OwnerReferenceBytes==null||r.OwnerReferenceBytes.Length==0)||
                    captured.Dimensions.Count!=dimensions.Length||captured.Dimensions.Select(d=>d?.Role).Distinct().Count()!=dimensions.Length)
                    throw new InvalidOperationException("Parametric capture ownership, configuration or role set is invalid.");
                if(rectangle&&(!captured.FullyDefined||!captured.RequiredRelationsPresent||captured.HasFixedContourEntities||captured.ContourEdgeCount!=4||captured.ConstructionEdgeCount!=1))
                    throw new InvalidOperationException("The native rectangle has not established its required constraints.");
                foreach(var dimension in captured.Dimensions)
                {
                    if(dimension==null||!dimensions.Contains(dimension.Role)||!dimension.IsDriving||double.IsNaN(dimension.ValueMm)||double.IsInfinity(dimension.ValueMm)||
                        dimension.EntityId!=CadParametricIdentity.For(modelId,sketch,dimension.Role)||
                        dimension.OwnerEntityId!=(rectangle?sketch:CadParametricIdentity.For(modelId,sketch,"boss")))
                        throw new InvalidOperationException("Required driving dimension ownership is invalid.");
                    var expected=dimension.Role=="width"?intent.WidthMm:dimension.Role=="length"?intent.LengthMm:intent.ThicknessMm;
                    if(Math.Abs(dimension.ValueMm-expected)>0.001)throw new InvalidOperationException("Native dimension differs from approved intent.");
                }
                foreach(var role in captured.Roles)
                {
                    bool dimension=dimensions.Contains(role.Role);
                    if((dimension&&(role.OwnerKind!="DimensionOwnerFeature"||string.IsNullOrWhiteSpace(role.NativeDimensionFullName)))||
                        (!dimension&&role.OwnerKind!=(role.Role=="boss"?"Feature":"SketchSegment")))
                        throw new InvalidOperationException("Captured role owner kind is invalid.");
                    var now=DateTime.UtcNow;
                    var fingerprint=new JObject { ["evidenceVersion"]=1,["role"]=role.Role,["modelId"]=modelId.ToString("D"),
                        ["sketchEntityId"]=sketch.ToString("D"),["configurationKey"]=captured.ConfigurationKey,
                        ["ownerEntityId"]=(rectangle?sketch:CadParametricIdentity.For(modelId,sketch,"boss")).ToString("D"),
                        ["nativeDimensionFullName"]=role.NativeDimensionFullName,
                        ["nativeDimensionName"]=dimension?role.NativeDimensionFullName.Split('@')[0]:null };
                    var binding=new CadEntityReferenceBinding { ModelId=modelId,EntityId=role.EntityId,EntityKind=dimension?"DrivingDimension":role.Role=="boss"?"Feature":"SketchSegment",
                        ConfigurationKey=captured.ConfigurationKey,NativeObjectKind=role.OwnerKind,ReferenceFormatVersion=3,NativeReferenceBytes=role.OwnerReferenceBytes,
                        CreatedAtModelRevisionId=context.Attempt.ProspectiveModelRevisionId,Status=CadEntityReferenceStatus.Active,CreatedUtc=now,UpdatedUtc=now,SemanticFingerprintJson=fingerprint.ToString() };
                    ValidateBinding(binding,false);
                    using(var command=connection.CreateCommand())
                    {
                        command.Transaction=transaction;
                        command.CommandText=@"INSERT INTO EntityReferenceBindings
(ModelId,EntityId,EntityKind,ConfigurationKey,NativeObjectKind,ReferenceFormatVersion,NativeReferenceBytes,CreatedAtModelRevisionId,LastResolvedModelRevisionId,SemanticFingerprintJson,Status,CreatedUtc,UpdatedUtc)
VALUES (@ModelId,@EntityId,@EntityKind,@ConfigurationKey,@NativeObjectKind,@ReferenceFormatVersion,@NativeReferenceBytes,@CreatedAtModelRevisionId,@LastResolvedModelRevisionId,@SemanticFingerprintJson,@Status,@CreatedUtc,@UpdatedUtc);";
                        BindEntityBinding(command,binding,CloneBytes(binding.NativeReferenceBytes));command.ExecuteNonQuery();
                    }
                }
                ApplyV2SequentialOutcome(connection,transaction,context,null,null);transaction.Commit();
            }
            return Task.CompletedTask;
        }
    }
}
