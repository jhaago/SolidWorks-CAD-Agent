using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SolidWorksCadAgent.Contracts.Design;
using SolidWorksCadAgent.Core.Ai;
namespace SolidWorksCadAgent.AgentHost.Design
{
    public sealed class DesignIntakeService
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private readonly DesignSessionRepository repository;
        private readonly ReferenceImageStore store;
        private readonly IImageDesignInterpreter interpreter;
        private readonly Func<string, CancellationToken, Task<Guid>> plan;

        public DesignIntakeService(DesignSessionRepository repository, ReferenceImageStore store, IImageDesignInterpreter interpreter, Func<string, CancellationToken, Task<Guid>> planApproved)
        {
            this.repository = repository;
            this.store = store;
            this.interpreter = interpreter;
            plan = planApproved;
        }

        private async Task<T> Locked<T>(CancellationToken token, Func<Task<T>> action)
        {
            await Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                return await action().ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }

        private DesignSession Require(Guid id) => repository.Get(id) ?? throw new DesignIntakeException("not_found", "Design session was not found.");

        private DesignSession Save(DesignSession s)
        {
            s.UpdatedUtc = DateTime.UtcNow;
            repository.Save(s);
            return s;
        }

        private static void Fail(string code, string message)
        {
            throw new DesignIntakeException(code, message);
        }

        public Task<DesignSession> CreateAsync(string title, CancellationToken token = default(CancellationToken)) => Locked(token, () =>
        {
            if (string.IsNullOrWhiteSpace(title) || title.Length > 200) Fail("invalid_title", "Provide a title of at most 200 characters.");
            var s = new DesignSession
            {
                Id = Guid.NewGuid(),
                Title = title.Trim(),
                CreatedUtc = DateTime.UtcNow
            };
            return Task.FromResult(Save(s));
        });

        public Task<DesignSession> GetAsync(Guid id, CancellationToken token = default(CancellationToken)) => Locked(token, () => Task.FromResult(Require(id)));

        public Task<List<DesignSession>> ListAsync(CancellationToken token = default(CancellationToken)) => Locked(token, () => Task.FromResult(repository.List()));

        public Task<DesignSession> AddReferenceAsync(Guid id, string filename, byte[] bytes, string label, string viewType, CancellationToken token = default(CancellationToken)) => Locked(token, () =>
        {
            var s = Require(id);
            if (s.CadJobId.HasValue) Fail("cad_already_planned", "This design already has a CAD job.");
            if (s.References.Count >= 8 || bytes == null || s.References.Sum(r => (long)r.ByteLength) + bytes.Length > 16 * 1024 * 1024) Fail("reference_limit", "Use at most eight references totaling 16 MiB.");
            if ((label ?? "").Length > 200 || (viewType ?? "").Length > 100) Fail("invalid_label", "Reference labels are too long.");
            s.References.Add(store.Save(id, filename, bytes, label, viewType));
            s.ApprovedRevisionId = null;
            s.LastError = null;
            s.State = "ImageReceived";
            return Task.FromResult(Save(s));
        });

        private static void Validate(DesignInterpretation b)
        {
            if (b == null || string.IsNullOrWhiteSpace(b.Summary) || b.Summary.Length > 16000) Fail("invalid_interpretation", "The interpretation is incomplete.");
            if ((b.ModellingStrategy ?? "").Length > 16000 || (b.Confidence ?? "").Length > 1000) Fail("invalid_interpretation", "The interpretation contains oversized fields.");
            foreach (var property in typeof(DesignInterpretation).GetProperties().Where(p => p.PropertyType == typeof(List<string>)))
            {
                var list = (List<string>)property.GetValue(b);
                if (list == null || list.Count > 100 || list.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 4000)) Fail("invalid_interpretation", "The interpretation contains invalid fields.");
            }
            if (b.ResolvedUnknowns == null || b.ResolvedUnknowns.Count > 100 || b.ResolvedUnknowns.Any(r =>
                r == null || string.IsNullOrWhiteSpace(r.Unknown) || r.Unknown.Length > 4000 ||
                string.IsNullOrWhiteSpace(r.Evidence) || r.Evidence.Length > 4000))
                Fail("invalid_interpretation", "The interpretation contains invalid unknown resolutions.");
            if (b.Questions == null || b.Questions.Count > 3 || b.Questions.Any(q => q == null || string.IsNullOrWhiteSpace(q.Id) || q.Id.Length > 100 || string.IsNullOrWhiteSpace(q.Question) || q.Question.Length > 4000 || (q.Answer ?? "").Length > 4000) || b.Questions.Select(q => q.Id).Distinct().Count() != b.Questions.Count) Fail("invalid_interpretation", "The interpretation contains invalid questions.");
        }

        public Task<DesignSession> DiscussAsync(Guid id, string message, CancellationToken token = default(CancellationToken)) => Locked(token, async () =>
        {
            var s = Require(id);
            if (s.CadJobId.HasValue) Fail("cad_already_planned", "This design already has a CAD job.");
            if (string.IsNullOrWhiteSpace(message) || message.Length > 4000) Fail("invalid_message", "Messages must contain at most 4000 characters.");
            if (s.Messages.Count >= 99) Fail("history_limit", "This session has reached its conversation limit.");
            if (s.References.Count == 0) Fail("reference_required", "Upload a reference image first.");
            s.Messages.Add(new DesignMessage
            {
                Role = "user",
                Text = message,
                CreatedUtc = DateTime.UtcNow
            });
            s.ApprovedRevisionId = null;
            s.LastError = null;
            s.State = "Interpreting";
            Save(s);
            try
            {
                var request = new ImageDesignRequest
                {
                    Design = JsonConvert.DeserializeObject<DesignSession>(JsonConvert.SerializeObject(s)),
                    Images = s.References.Select(r => new DesignReferenceContent
                    {
                        Reference = JsonConvert.DeserializeObject<DesignReference>(JsonConvert.SerializeObject(r)),
                        Bytes = store.Read(r)
                    }
).ToList()
                };
                var b = await interpreter.InterpretAsync(request, token).ConfigureAwait(false);
                Validate(b);
                b = JsonConvert.DeserializeObject<DesignInterpretation>(JsonConvert.SerializeObject(b));
                var previous = s.Revisions.LastOrDefault()?.Brief;
                if (previous != null)
                {
                    foreach (var q in previous.Questions.Where(q => q.Critical && string.IsNullOrWhiteSpace(q.Answer)))
                    {
                        var next = b.Questions.FirstOrDefault(n => n.Id == q.Id);
                        if (next == null) b.Questions.Insert(0, q);
                        else next.Critical = true;
                    }
                    if (b.Questions.Count > 3) Fail("invalid_interpretation", "Resolve existing critical questions before adding more questions.");
                    foreach (var unknown in previous.Unknowns)
                    {
                        bool explicitlyResolved = b.ResolvedUnknowns.Any(resolution =>
                            resolution.Unknown == unknown &&
                            s.Messages.Any(m => m.Role == "user" && m.Text.IndexOf(resolution.Evidence, StringComparison.OrdinalIgnoreCase) >= 0));
                        if (!b.Unknowns.Contains(unknown) && !explicitlyResolved)
                            b.Unknowns.Add(unknown);
                    }
                }
                // Answers are accepted only when explicitly present in the user's conversation.
                foreach (var q in b.Questions.Where(q => !string.IsNullOrWhiteSpace(q.Answer))) if (!s.Messages.Where(m => m.Role == "user").Any(m => m.Text.IndexOf(q.Answer, StringComparison.OrdinalIgnoreCase) >= 0)) q.Answer = null;
                s.Revisions.Add(new DesignBriefRevision
                {
                    Id = Guid.NewGuid(),
                    Number = s.Revisions.Count + 1,
                    CreatedUtc = DateTime.UtcNow,
                    ReferenceIds = s.References.Select(r => r.Id).ToList(),
                    Brief = b
                });
                s.Messages.Add(new DesignMessage
                {
                    Role = "assistant",
                    Text = b.Summary,
                    CreatedUtc = DateTime.UtcNow
                });
                s.State = b.MissingDimensions.Count == 0 && !b.Questions.Any(q => q.Critical && string.IsNullOrWhiteSpace(q.Answer)) ? "AwaitingDesignApproval" : "NeedsClarification";
            }
            catch (OperationCanceledException)
            {
                s.LastError = "Interpretation was cancelled. Retry to continue.";
                s.State = "NeedsClarification";
                Save(s);
                throw;
            }
            catch
            {
                s.LastError = "The reference interpretation could not be completed. Check your references and try again.";
                s.State = "NeedsClarification";
            }
            return Save(s);
        });

        private static DesignBriefRevision Current(DesignSession s, Guid revisionId)
        {
            var r = s.Revisions.LastOrDefault();
            if (r == null || r.Id != revisionId) Fail("stale_revision", "Select the current design revision.");
            return r;
        }

        public Task<DesignSession> ApproveAsync(Guid id, Guid revisionId, CancellationToken token = default(CancellationToken)) => Locked(token, () =>
        {
            var s = Require(id);
            Current(s, revisionId);
            if (s.State != "AwaitingDesignApproval" || s.LastError != null) Fail("not_ready", "Resolve critical questions and missing dimensions before approval.");
            s.ApprovedRevisionId = revisionId;
            s.State = "DesignApproved";
            return Task.FromResult(Save(s));
        });

        public Task<DesignSession> PlanCadAsync(Guid id, Guid revisionId, CancellationToken token = default(CancellationToken)) => Locked(token, async () =>
        {
            var s = Require(id);
            var r = Current(s, revisionId);
            if (s.CadJobId.HasValue) Fail("cad_already_planned", "This design already has a CAD job.");
            if (s.ApprovedRevisionId != revisionId || s.State != "DesignApproved" || s.LastError != null) Fail("approval_required", "Approve the current design revision first.");
            if (r.Brief.UnsupportedFeatures.Count != 0) Fail("unsupported_features", "This design requires unsupported CAD features.");
            var brief = "Create a CAD plan from this explicitly approved design brief. Require separate CAD plan approval before execution.\n" + JsonConvert.SerializeObject(r.Brief);
            s.CadJobId = await plan(brief, token).ConfigureAwait(false);
            s.State = "CADPlanCreated";
            return Save(s);
        });
    }
}



