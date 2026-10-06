using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.AgentHost.Design;
using SolidWorksCadAgent.Contracts.Design;
using SolidWorksCadAgent.Core.Ai;
namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class DesignIntakeServiceTests
    {
        internal static byte[] ImageBytes => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jB1sAAAAASUVORK5CYII=");
        private sealed class Provider : IImageDesignInterpreter
        {
            public DesignInterpretation Result = new DesignInterpretation
            {
                Summary = "A plate"
            };
            public ImageDesignRequest Request;
            public bool Fail;
            public Task<DesignInterpretation> InterpretAsync(ImageDesignRequest request, CancellationToken token)
            {
                Request = request;
                if (Fail) throw new Exception("secret provider diagnostics");
                return Task.FromResult(Result);
            }
        }
        [TestMethod]
        public async Task OmittedCriticalQuestionsResolveOnlyWithMatchingUserEvidence()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var repo = new DesignSessionRepository(Path.Combine(dir, "design-intake.db"));
                repo.Initialize();
                var provider = new Provider();
                foreach (string id in new[] { "width", "height", "thickness" })
                    provider.Result.Questions.Add(new DesignQuestion { Id = id, Question = "Specify " + id, Critical = true });
                var service = new DesignIntakeService(repo, new ReferenceImageStore(dir), provider,
                    (brief, token) => Task.FromResult(Guid.NewGuid()));
                var session = await service.CreateAsync("Plate");
                await service.AddReferenceAsync(session.Id, "front.png", ImageBytes, "", "");
                session = await service.DiscussAsync(session.Id, "Interpret this plate");
                Guid initialRevision = session.Revisions[0].Id;
                provider.Result = new DesignInterpretation { Summary = "Plate with dimensions" };
                provider.Result.ResolvedQuestions.Add(new DesignQuestionResolution { QuestionId = "material", Evidence = "steel" });
                provider.Result.ResolvedQuestions.Add(new DesignQuestionResolution { QuestionId = "width", Evidence = "Width is 999 mm" });
                session = await service.DiscussAsync(session.Id, "Use steel");
                Assert.AreEqual("NeedsClarification", session.State);
                Assert.AreEqual(3, session.Revisions[1].Brief.Questions.Count);
                Assert.AreEqual(0, session.Revisions[1].Brief.ResolvedQuestions.Count);
                provider.Result = new DesignInterpretation { Summary = "Plate with confirmed dimensions" };
                foreach (string id in new[] { "width", "height", "thickness" })
                    provider.Result.ResolvedQuestions.Add(new DesignQuestionResolution
                    {
                        QuestionId = id,
                        Evidence = "Width 100 mm, height 60 mm, thickness 10 mm"
                    });
                session = await service.DiscussAsync(session.Id, "Width 100 mm, height 60 mm, thickness 10 mm");
                Assert.AreEqual("AwaitingDesignApproval", session.State);
                Assert.AreEqual(0, session.Revisions[2].Brief.Questions.Count);
                Assert.AreEqual(3, session.Revisions[2].Brief.ResolvedQuestions.Count);
                Assert.AreEqual(initialRevision, session.Revisions[0].Id);
                Assert.IsTrue(session.Revisions[0].Brief.Questions.TrueForAll(q => q.Answer == null));
                await service.ApproveAsync(session.Id, session.Revisions[2].Id);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        [TestMethod]
        public async Task UnknownResolutionRequiresMatchingUnknownAndUserEvidence()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var repo = new DesignSessionRepository(Path.Combine(dir, "design-intake.db"));
                repo.Initialize();
                var provider = new Provider();
                provider.Result.Unknowns.Add("Hole spacing");
                provider.Result.Unknowns.Add("Width");
                var service = new DesignIntakeService(repo, new ReferenceImageStore(dir), provider,
                    (brief, token) => Task.FromResult(Guid.NewGuid()));
                var session = await service.CreateAsync("Bracket");
                await service.AddReferenceAsync(session.Id, "front.png", ImageBytes, "front", "front");
                await service.DiscussAsync(session.Id, "Please interpret this bracket");
                provider.Result = new DesignInterpretation { Summary = "Steel bracket" };
                provider.Result.Questions.Add(new DesignQuestion
                {
                    Id = "material",
                    Question = "What material?",
                    Answer = "steel"
                });
                session = await service.DiscussAsync(session.Id, "Use steel");
                CollectionAssert.AreEquivalent(new[] { "Hole spacing", "Width" }, session.Revisions[1].Brief.Unknowns);
                provider.Result = new DesignInterpretation { Summary = "A measured bracket" };
                provider.Result.ResolvedUnknowns.Add(new DesignUnknownResolution
                {
                    Unknown = "Hole spacing",
                    Evidence = "Hole spacing is 30 mm"
                });
                provider.Result.ResolvedUnknowns.Add(new DesignUnknownResolution
                {
                    Unknown = "Width",
                    Evidence = "Width is 40 mm"
                });
                session = await service.DiscussAsync(session.Id, "Hole spacing is 30 mm");
                CollectionAssert.AreEquivalent(new[] { "Width" }, session.Revisions[2].Brief.Unknowns);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        [TestMethod]
        public async Task ConversationPersistsRevisionsAndApprovalGuardsCad()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var repo = new DesignSessionRepository(Path.Combine(dir, "design-intake.db"));
                repo.Initialize();
                var p = new Provider();
                int calls = 0;
                string capturedPrompt = null;
                var service = new DesignIntakeService(repo, new ReferenceImageStore(dir), p, (brief, t) =>
                {
                    calls++;
                    capturedPrompt = brief;
                    return Task.FromResult(Guid.NewGuid());
                });
                var s = await service.CreateAsync("Plate");
                s = await service.AddReferenceAsync(s.Id, "../plate.png", ImageBytes, "front", "front");
                s = await service.DiscussAsync(s.Id, "Make a 10 mm plate");
                Assert.AreEqual("AwaitingDesignApproval", s.State);
                Guid old = s.Revisions[0].Id;
                await Assert.ThrowsExceptionAsync<DesignIntakeException>(() => service.PlanCadAsync(s.Id, old));
                Assert.AreEqual(0, calls);
                await service.ApproveAsync(s.Id, old);
                s = await service.DiscussAsync(s.Id, "Change thickness to 12 mm");
                Assert.IsNull(s.ApprovedRevisionId);
                Assert.AreEqual(2, s.Revisions.Count);
                Assert.AreEqual(3, p.Request.Design.Messages.Count);
                await Assert.ThrowsExceptionAsync<DesignIntakeException>(() => service.ApproveAsync(s.Id, old));
                var current = s.Revisions[1].Id;
                await service.ApproveAsync(s.Id, current);
                s = await service.PlanCadAsync(s.Id, current);
                Assert.AreEqual(1, calls);
                Assert.IsTrue(capturedPrompt.Contains(s.Id.ToString()));
                Assert.IsTrue(capturedPrompt.Contains(current.ToString()));
                Assert.IsTrue(capturedPrompt.Contains(s.References[0].Id.ToString()));
                Assert.IsTrue(capturedPrompt.Contains("\"RevisionNumber\":2"));
                Assert.IsFalse(capturedPrompt.Contains(old.ToString()));
                Assert.IsFalse(capturedPrompt.Contains(s.References[0].RelativePath));
                Assert.IsTrue(repo.Get(s.Id).CadJobId.HasValue);
                await Assert.ThrowsExceptionAsync<DesignIntakeException>(() => service.PlanCadAsync(s.Id, current));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        [TestMethod]
        public async Task CriticalQuestionsAndUnsupportedFeaturesPreventCadPlanning()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var repo = new DesignSessionRepository(Path.Combine(dir, "design-intake.db"));
                repo.Initialize();
                var p = new Provider();
                p.Result.MissingDimensions.Add("width");
                p.Result.Questions.Add(new DesignQuestion
                {
                    Id = "width",
                    Question = "What width?",
                    Critical = true
                });
                var service = new DesignIntakeService(repo, new ReferenceImageStore(dir), p, (b, t) =>
                {
                    Assert.Fail("CAD callback must not run");
                    return Task.FromResult(Guid.NewGuid());
                });
                var s = await service.CreateAsync("Test");
                await service.AddReferenceAsync(s.Id, "a.png", ImageBytes, "", "");
                await service.AddReferenceAsync(s.Id, "b.png", ImageBytes, "", "");
                s = await service.DiscussAsync(s.Id, "A bracket");
                Assert.AreEqual(2, p.Request.Images.Count);
                Assert.AreEqual("NeedsClarification", s.State);
                await Assert.ThrowsExceptionAsync<DesignIntakeException>(() => service.ApproveAsync(s.Id, s.Revisions[0].Id));
                p.Result = new DesignInterpretation
                {
                    Summary = "A bracket"
                };
                s = await service.DiscussAsync(s.Id, "Use steel");
                Assert.AreEqual("NeedsClarification", s.State);
                Assert.AreEqual("width", s.Revisions[1].Brief.Questions[0].Id);
                p.Result = new DesignInterpretation
                {
                    Summary = "A bracket"
                };
                p.Result.Questions.Add(new DesignQuestion
                {
                    Id = "width",
                    Question = "What width?",
                    Critical = true,
                    Answer = "20 mm"
                });
                p.Result.UnsupportedFeatures.Add("loft");
                s = await service.DiscussAsync(s.Id, "20 mm");
                Assert.AreEqual("AwaitingDesignApproval", s.State);
                await service.ApproveAsync(s.Id, s.Revisions[2].Id);
                await Assert.ThrowsExceptionAsync<DesignIntakeException>(() => service.PlanCadAsync(s.Id, s.Revisions[2].Id));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        [TestMethod]
        public async Task ProviderFailureRetainsUserMessageAndClearsApproval()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var r = new DesignSessionRepository(Path.Combine(dir, "design-intake.db"));
                r.Initialize();
                var p = new Provider
                {
                    Fail = true
                };
                var service = new DesignIntakeService(r, new ReferenceImageStore(dir), p, (b, t) => Task.FromResult(Guid.NewGuid()));
                var s = await service.CreateAsync("Test");
                await service.AddReferenceAsync(s.Id, "a.png", ImageBytes, "", "");
                s = await service.DiscussAsync(s.Id, "User details");
                Assert.AreEqual("NeedsClarification", s.State);
                Assert.AreEqual("User details", r.Get(s.Id).Messages[0].Text);
                Assert.IsFalse(s.LastError.Contains("secret"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
    [TestClass]
    public class ReferenceImageStoreTests
    {
        [TestMethod]
        public void DecodesJpegAndChecksTypeAndMetadata()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new ReferenceImageStore(dir);
                var sessionId = Guid.NewGuid();
                byte[] bytes;
                using (var bitmap = new System.Drawing.Bitmap(4, 3)) using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
                    bytes = stream.ToArray();
                }
                var before = DateTime.UtcNow;
                var reference = store.Save(sessionId, "front.jpeg", bytes, "front label", "front");
                Assert.AreEqual("image/jpeg", reference.MediaType);
                Assert.AreEqual(bytes.Length, reference.ByteLength);
                Assert.AreEqual("front label", reference.Label);
                Assert.AreEqual("front", reference.ViewType);
                Assert.AreEqual(64, reference.Sha256.Length);
                Assert.IsTrue(reference.AddedUtc >= before);
                Assert.IsFalse(Path.IsPathRooted(reference.RelativePath));
                CollectionAssert.AreEqual(bytes, store.Read(reference));
                Assert.ThrowsException<DesignIntakeException>(() => store.Save(sessionId, "image.exe", DesignIntakeServiceTests.ImageBytes, "", ""));
                Assert.ThrowsException<DesignIntakeException>(() => store.Save(sessionId, "image.jpg", DesignIntakeServiceTests.ImageBytes, "", ""));
                Assert.ThrowsException<DesignIntakeException>(() => store.Save(sessionId, "image.png", new byte[]{
137,80,78,71,13,10,26,10,0,1,2}
            , "", ""));
                bytes[bytes.Length - 1] ^= 1;
                File.WriteAllBytes(Path.Combine(dir, reference.RelativePath), bytes);
                Assert.ThrowsException<DesignIntakeException>(() => store.Read(reference));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        [TestMethod]
        public void RejectsInvalidOversizeAndChangedReferences()
        {
            string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new ReferenceImageStore(dir);
                Assert.ThrowsException<DesignIntakeException>(() => store.Save(Guid.NewGuid(), "a.png", new byte[]{
1,2,3}
               , "", ""));
                Assert.ThrowsException<DesignIntakeException>(() => store.Save(Guid.NewGuid(), "a.png", new byte[ReferenceImageStore.MaxImageBytes + 1], "", ""));
                var r = store.Save(Guid.NewGuid(), "../safe.png", DesignIntakeServiceTests.ImageBytes, "", "");
                Assert.AreEqual("safe.png", r.FileName);
                CollectionAssert.AreEqual(DesignIntakeServiceTests.ImageBytes, store.Read(r));
                File.Delete(Path.Combine(dir, r.RelativePath));
                Assert.ThrowsException<DesignIntakeException>(() => store.Read(r));
                r.RelativePath = "../outside.png";
                Assert.ThrowsException<DesignIntakeException>(() => store.Read(r));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}




