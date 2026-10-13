using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Host;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.AgentHost.Persistence;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core;
using SolidWorksCadAgent.Core.Commands;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass, DoNotParallelize, TestCategory("SolidWorksIntegration")]
    public class CheckpointedParametricEditBoundaryTests
    {
        [DataTestMethod]
        [DataRow("source-before-copy")]
        [DataRow("source-before-mutation")]
        [DataRow("evidence-before-mutation")]
        [DataRow("edit-before-mutation")]
        [DataRow("cancel-after-close")]
        [DataRow("failed-reopen")]
        [Timeout(240000)]
        public async Task RevokedSideEffectsAndClosedChildRecoveryFailSafely(string boundary)
        {
            if (Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS") != "1") Assert.Inconclusive("Native opt-in required.");
            var root = Path.Combine("C:/SolidWorks-CAD-Agent/Workspace/capability-tests", "parametric-edit-boundary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            using (var repository = new SqliteJobRepository(Path.Combine(root, "jobs.sqlite")))
            using (var session = new SolidWorksSession())
            using (var bridge = new SolidWorksBridgeFacade(session, new WorkspacePolicy(root), repository))
            using (var cancellation = new CancellationTokenSource())
            {
                await repository.InitializeAsync(); Assert.IsTrue((await session.AttachAsync(CancellationToken.None)).IsConnected);
                var sourceCoordinator = new JobCoordinator(repository, new CheckpointedParametricEditNativeTests.NoPlanner(), bridge,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real });
                var filename = Path.GetFileName(root) + "-source.sldprt";
                var source = await sourceCoordinator.CreateAndPlanAsync("V2 parametric plate: 120 x 60 x 10 mm; save as " + filename, CancellationToken.None);
                source = await sourceCoordinator.ApproveAndExecuteAsync(source.Job.Id, source.Revisions[0].Id, CancellationToken.None);
                Assert.AreEqual(JobState.ReadyForReview, source.Job.State);
                var sourceHash = CheckpointedParametricEditorHash.HashFile(Path.Combine(root, filename));
                var fault = new BoundaryExecutor(bridge, repository, source.Job.Id, boundary, cancellation);
                var coordinator = new JobCoordinator(repository, new CheckpointedParametricEditNativeTests.NoPlanner(), fault,
                    new AgentSettings { WorkspaceRoot = root, ExecutionMode = ExecutionMode.Real });
                var edit = await coordinator.CreateAndPlanAsync("V2 parametric edit: job " + source.Job.Id + "; parameter width = 150 mm; save as " + Path.GetFileName(root) + "-result.sldprt", CancellationToken.None);
                fault.EditJob = edit.Job.Id;
                try { await coordinator.ApproveAndExecuteAsync(edit.Job.Id, edit.Revisions[0].Id, cancellation.Token); }
                catch (OperationCanceledException) { Assert.AreEqual("cancel-after-close", boundary); }
                var attempt = await repository.GetParametricEditAttemptAsync(edit.Job.Id, edit.Revisions[0].Id, CancellationToken.None);
                Assert.IsTrue(fault.Injected);
                Assert.AreEqual(sourceHash, CheckpointedParametricEditorHash.HashFile(Path.Combine(root, filename)));
                if (boundary == "source-before-copy")
                {
                    Assert.IsFalse(File.Exists(attempt.WorkingPath), "Revoked source must be rejected before copying bytes.");
                    Assert.IsFalse(File.Exists(attempt.CheckpointPath));
                    Assert.AreEqual(CadParametricEditAttemptStatus.Uncertain, attempt.Status);
                }
                else
                {
                    if (boundary.EndsWith("before-mutation")) Assert.IsFalse(fault.MutationReturned, "Authorization revocation must prevent native mutation, not merely publication.");
                    Assert.AreEqual(CadParametricEditAttemptStatus.Restored, attempt.Status, "An intact checkpoint must restore even after the owned document closes.");
                    var child = await repository.GetModelAsync(attempt.ModelId, CancellationToken.None);
                    Assert.AreEqual(attempt.CheckpointSha256, CheckpointedParametricEditorHash.HashFile(child.CanonicalPath));
                    Assert.AreEqual(attempt.CheckpointModelRevisionId, child.CurrentModelRevisionId);
                    await bridge.ExecuteAsync(new CadCommandEnvelope { Command = "CloseDocument", ExecutionId = edit.Job.Id, ManagedModelId = attempt.ModelId, Parameters = new JObject() }, CancellationToken.None);
                }
                var routes = new AgentRoutes(repository, session, coordinator);
                Assert.AreEqual(409, (await routes.HandleAsync(new AgentRequest("GET", "/jobs/" + edit.Job.Id + "/artifact", null), CancellationToken.None)).StatusCode);
                var sourceModel = Guid.Parse(JObject.Parse(source.Verifications[source.Verifications.Count-1].ExpectedJson).Value<string>("modelId"));
                Assert.IsTrue((await bridge.ExecuteAsync(new CadCommandEnvelope { Command = "OpenPart", ExecutionId = source.Job.Id, Parameters = new JObject { { "path", filename } } }, CancellationToken.None)).Success);
                Assert.IsTrue((await bridge.ExecuteAsync(new CadCommandEnvelope { Command = "CloseDocument", ExecutionId = source.Job.Id, ManagedModelId = sourceModel, Parameters = new JObject() }, CancellationToken.None)).Success);
            }
        }

        private sealed class BoundaryExecutor : ICadCommandExecutor, IVersionedCadCommandExecutor, ICadOperationAvailability, ICheckpointedParametricEditExecutor
        {
            private readonly SolidWorksBridgeFacade _inner;
            private readonly SqliteJobRepository _repository;
            private readonly Guid _source;
            private readonly string _boundary;
            private readonly CancellationTokenSource _cancellation;
            public Guid EditJob;
            public bool Injected, MutationReturned;
            public BoundaryExecutor(SolidWorksBridgeFacade inner, SqliteJobRepository repository, Guid source, string boundary, CancellationTokenSource cancellation)
            { _inner = inner; _repository = repository; _source = source; _boundary = boundary; _cancellation = cancellation; }
            public bool SupportsManagedReferences => _inner.SupportsManagedReferences;
            public bool SupportsCheckpointedParametricEditing => _inner.SupportsCheckpointedParametricEditing;
            public bool IsOperationRegistered(string name, int version) => _inner.IsOperationRegistered(name, version);
            private async Task Revoke(Guid job)
            { var current = await _repository.GetAsync(job); current.State = JobState.Cancelled; await _repository.UpdateAsync(current); Injected = true; }
            public async Task<CadCommandResult> ExecuteAsync(CadCommandEnvelope command, CancellationToken token)
            {
                if (_boundary == "failed-reopen" && command.Command == "OpenPart")
                { Injected = true; return new CadCommandResult { Success = false, Error = new CadError { Code = "INJECTED_REOPEN_FAILURE", Message = "Owned output reopening failed." } }; }
                var result = await _inner.ExecuteAsync(command, token);
                if (_boundary == "cancel-after-close" && command.Command == "CloseDocument" && result.Success)
                { await Revoke(EditJob); _cancellation.Cancel(); }
                return result;
            }
            public Task<CadCommandResult> ExecuteVersionedAsync(CadVersionedCommandRequest request, CancellationToken token) => _inner.ExecuteVersionedAsync(request, token);
            public Task<CadParametricEditCheckpoint> ForkParametricPartAsync(CadParametricEditNativeRequest request, CancellationToken token)
            {
                var authorize = request.AuthorizeSideEffect; var calls = 0;
                request.AuthorizeSideEffect = () => {
                    calls++;
                    if (calls == 1 && _boundary == "source-before-copy" || calls == 2 && _boundary == "source-before-mutation") Revoke(_source).GetAwaiter().GetResult();
                    if (calls == 2 && _boundary == "edit-before-mutation") Revoke(EditJob).GetAwaiter().GetResult();
                    if (calls == 2 && _boundary == "evidence-before-mutation") {
                        var source = _repository.GetSnapshotAsync(_source).GetAwaiter().GetResult();
                        var latest = System.Linq.Enumerable.Last(source.Verifications, v => v.CheckName == CadV2SavedVerificationRecorder.CheckName);
                        _repository.AppendVerificationAsync(new VerificationResultRecord { Id = Guid.NewGuid(), JobId = _source, RevisionNumber = latest.RevisionNumber,
                            CheckName = latest.CheckName, Passed = false, ExpectedJson = latest.ExpectedJson, ActualJson = "{}", CreatedUtc = DateTime.UtcNow }).GetAwaiter().GetResult();
                        Injected = true;
                    }
                    authorize();
                };
                return _inner.ForkParametricPartAsync(request, token);
            }
            public async Task<JObject> ApplyCheckpointedParameterAsync(Guid job, Guid model, CadPlanV2Document plan, CancellationToken token)
            {
                var observed = await _inner.ApplyCheckpointedParameterAsync(job, model, plan, token); MutationReturned = true; return observed;
            }
            public Task<JObject> InspectCheckpointedPartAsync(Guid job, Guid model, CadPlanV2Document plan, CancellationToken token) => _inner.InspectCheckpointedPartAsync(job, model, plan, token);
            public Task<JObject> RestoreParametricCheckpointAsync(Guid job, Guid model, string hash, CancellationToken token) => _inner.RestoreParametricCheckpointAsync(job, model, hash, token);
        }
    }
}
