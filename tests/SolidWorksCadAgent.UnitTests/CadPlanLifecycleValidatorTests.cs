using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadPlanLifecycleValidatorTests
    {
        [TestMethod]
        public void SequentialPartSketchFeaturesAndFinalizationAreAccepted()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddRectangle),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddCircle),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.CutExtrude),
                Command(CadCommandNames.Rebuild),
                Command(CadCommandNames.SavePart),
                Command(CadCommandNames.CloseDocument)
            });

            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void ProfileGeometryRequiresSketchOpenedInPlan()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.AddLine)
            });

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "open sketch");
        }

        [TestMethod]
        public void UnknownInitialDocumentRemainsCompatibleWithActiveDocumentPlans()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddLine),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.SavePart)
            });

            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void ExistingOpenedPartCannotBeModifiedBeforeManagedCopyExists()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.OpenPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddRectangle),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude),
                Command(CadCommandNames.SavePart)
            });

            Assert.AreEqual(5, errors.Count);
            StringAssert.Contains(errors[0], "read-only");
            StringAssert.Contains(errors[4], "managed working copy");
        }

        [TestMethod]
        public void NewPartAfterReadOnlyOpenRestoresManagedMutationPath()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.OpenPart),
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddRectangle),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude)
            });

            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void DuplicateSketchAndExitWithoutSketchAreRejected()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.ExitSketch)
            });

            Assert.AreEqual(2, errors.Count);
            StringAssert.Contains(errors[0], "already open");
            StringAssert.Contains(errors[1], "no open sketch");
        }

        [TestMethod]
        public void FeatureAndFinalizationRequireClosedSketch()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddRectangle),
                Command(CadCommandNames.Extrude),
                Command(CadCommandNames.SavePart),
                Command(CadCommandNames.CloseDocument)
            });

            Assert.AreEqual(3, errors.Count);
            StringAssert.Contains(errors[0], "ExitSketch");
            StringAssert.Contains(errors[1], "ExitSketch");
            StringAssert.Contains(errors[2], "ExitSketch");
        }

        [TestMethod]
        public void ClosedDocumentCannotBeUsedAgainWithoutOpeningPart()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CloseDocument),
                Command(CadCommandNames.CloseDocument),
                Command(CadCommandNames.CreateSketch)
            });

            Assert.AreEqual(2, errors.Count);
            StringAssert.Contains(errors[0], "no open document to close");
            StringAssert.Contains(errors[1], "closed the document");
        }

        [TestMethod]
        public void FeatureRequiresACompletedSupportedClosedProfile()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddLine),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude)
            });

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "supported closed profile primitive");
        }

        [TestMethod]
        public void FeatureConsumesCompletedProfileOnlyOnce()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddCircle),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude),
                Command(CadCommandNames.CutExtrude)
            });

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "supported closed profile primitive");
        }

        [TestMethod]
        public void EmptyClosedSketchDoesNotProvideFeatureProfile()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.CutExtrude)
            });

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "supported closed profile primitive");
        }

        [TestMethod]
        public void SavePartMayBeFollowedByClosingTheSavedDocument()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddRectangle),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude),
                Command(CadCommandNames.SavePart),
                Command(CadCommandNames.CloseDocument)
            });

            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void SavePartRejectsSubsequentModelChangingCommands()
        {
            var errors = CadPlanLifecycleValidator.Validate(new[]
            {
                Command(CadCommandNames.NewPart),
                Command(CadCommandNames.CreateSketch),
                Command(CadCommandNames.AddRectangle),
                Command(CadCommandNames.ExitSketch),
                Command(CadCommandNames.Extrude),
                Command(CadCommandNames.SavePart),
                Command(CadCommandNames.CreateSketch)
            });

            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains(errors[0], "final model-changing operation");
        }

        private static CadCommandEnvelope Command(string name) => new CadCommandEnvelope { Command = name };
    }
}
