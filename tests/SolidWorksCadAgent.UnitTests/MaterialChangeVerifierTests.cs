using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class MaterialChangeVerifierTests
    {
        [TestMethod] public void EmptyPartHasZeroVolume()
        {
            var state = SolidMaterialSnapshot.FromBodyVolumesMetresCubed(new double[0]);
            Assert.AreEqual(0, state.BodyCount); Assert.AreEqual(0.0, state.VolumeMm3);
        }

        [TestMethod] public void AllSolidVolumesAreSummedInMillimetresCubed()
        {
            var state = SolidMaterialSnapshot.FromBodyVolumesMetresCubed(new[] { 0.000060, 0.000002 });
            Assert.AreEqual(2, state.BodyCount); Assert.AreEqual(62000.0, state.VolumeMm3, 1e-8);
        }

        [DataTestMethod]
        [DataRow(0.0)] [DataRow(-1.0)] [DataRow(double.NaN)]
        [DataRow(double.PositiveInfinity)] [DataRow(double.NegativeInfinity)] [DataRow(double.MaxValue)]
        public void InvalidSolidMeasurementIsRejected(double volume) =>
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => State(volume));

        [TestMethod] public void NullMeasurementsAreNotAnEmptyPart() =>
            Assert.ThrowsException<ArgumentNullException>(() => SolidMaterialSnapshot.FromBodyVolumesMetresCubed(null));

        [DataTestMethod] [DataRow(0)] [DataRow(3)] [DataRow(4)] [DataRow(11)] [DataRow(13)]
        public void IncompleteOrUnexpectedNativeMassArrayIsRejected(int length) =>
            Assert.ThrowsException<ArgumentException>(() => SolidMaterialSnapshot.FromBodyMassProperties(new[] { new double[length] }));

        [TestMethod] public void NullNativeMassArrayIsRejected() =>
            Assert.ThrowsException<ArgumentException>(() => SolidMaterialSnapshot.FromBodyMassProperties(new double[][] { null }));

        [TestMethod] public void CompleteNativeMassArraysUseDocumentedVolumeIndex()
        {
            var mass = new double[12]; mass[3] = 0.000060; mass[4] = 0.1;
            Assert.AreEqual(60000.0, SolidMaterialSnapshot.FromBodyMassProperties(new[] { mass }).VolumeMm3, 1e-8);
        }

        [TestMethod] public void NonfiniteNativeFieldsCannotProduceSuccessfulEvidence()
        {
            var mass = new double[12]; mass[3] = 0.000060; mass[11] = double.NaN;
            Assert.ThrowsException<ArgumentException>(() => SolidMaterialSnapshot.FromBodyMassProperties(new[] { mass }));
        }

        [TestMethod] public void OverflowingAggregateIsRejected() =>
            Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
                SolidMaterialSnapshot.FromBodyVolumesMetresCubed(new[] { 1e299, 1e299 }));

        [TestMethod] public void BossAddsMaterialAndReturnsEvidence()
        {
            var result = MaterialChangeVerifier.Verify(State(), State(0.000060), MaterialChangeKind.Add);
            Assert.AreEqual(60000.0, result.VolumeDeltaMm3, 1e-8);
            Assert.AreEqual(1, result.BodyCountDelta); Assert.AreEqual("added", result.Material);
        }

        [TestMethod] public void CutRemovesMaterialWithoutChangingBodyCount()
        {
            var result = MaterialChangeVerifier.Verify(State(0.000060), State(0.000059), MaterialChangeKind.Remove);
            Assert.AreEqual(-1000.0, result.VolumeDeltaMm3, 1e-8); Assert.AreEqual(0, result.BodyCountDelta);
        }

        [TestMethod] public void BodyCountIncreaseDoesNotExcuseWrongSignVolume() =>
            Assert.ThrowsException<InvalidOperationException>(() => MaterialChangeVerifier.Verify(
                State(0.000060), State(0.000020, 0.000020), MaterialChangeKind.Add));

        [DataTestMethod] [DataRow(MaterialChangeKind.Add)] [DataRow(MaterialChangeKind.Remove)]
        public void NoMaterialChangeFails(MaterialChangeKind kind) =>
            Assert.ThrowsException<InvalidOperationException>(() => MaterialChangeVerifier.Verify(State(1e-5), State(1e-5), kind));

        [TestMethod] public void WrongDirectionCutFails() =>
            Assert.ThrowsException<InvalidOperationException>(() => MaterialChangeVerifier.Verify(State(1e-5), State(2e-5), MaterialChangeKind.Remove));

        [TestMethod] public void NumericalNoiseDoesNotCountAsMaterial() =>
            Assert.ThrowsException<InvalidOperationException>(() => MaterialChangeVerifier.Verify(State(1.0), State(1.0 + 1e-14), MaterialChangeKind.Add));

        [TestMethod] public void InvalidRuleFails() =>
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => MaterialChangeVerifier.Verify(State(), State(1e-5), (MaterialChangeKind)42));

        [TestMethod] public void BeforeMeasurementFailureDoesNotExecuteMutation()
        {
            bool called = false;
            var error = Assert.ThrowsException<FeatureMaterialVerificationException>(() => FeatureMaterialOperation.Run(
                MaterialChangeKind.Add, () => throw new InvalidOperationException("measurement"),
                () => { called = true; return new object(); }, _ => { }));
            Assert.IsFalse(called); Assert.IsFalse(error.MutationAttempted);
        }

        [TestMethod] public void MutationIsNeverRetriedAndFailureDisclosesPartialState()
        {
            int calls = 0;
            var error = Assert.ThrowsException<FeatureMaterialVerificationException>(() => FeatureMaterialOperation.Run<object>(
                MaterialChangeKind.Add, () => State(), () => { calls++; throw new InvalidOperationException("COM"); }, _ => { }));
            Assert.AreEqual(1, calls); Assert.IsTrue(error.MutationAttempted);
        }

        [TestMethod] public void HealthFailurePreventsSuccess()
        {
            var error = Assert.ThrowsException<FeatureMaterialVerificationException>(() => FeatureMaterialOperation.Run(
                MaterialChangeKind.Add, () => State(), () => new object(), _ => throw new InvalidOperationException("rebuild")));
            Assert.IsTrue(error.MutationAttempted); StringAssert.Contains(error.Message, "rebuild");
        }

        [TestMethod] public void MissingFeatureFails()
        {
            var error = Assert.ThrowsException<FeatureMaterialVerificationException>(() => FeatureMaterialOperation.Run<object>(
                MaterialChangeKind.Add, () => State(), () => null, _ => Assert.Fail("No feature to inspect")));
            Assert.IsTrue(error.MutationAttempted);
        }

        [TestMethod] public void AfterMeasurementFailureIsPartial()
        {
            int reads = 0;
            var error = Assert.ThrowsException<FeatureMaterialVerificationException>(() => FeatureMaterialOperation.Run(
                MaterialChangeKind.Add, () => ++reads == 1 ? State() : throw new InvalidOperationException("after"),
                () => new object(), _ => { }));
            Assert.AreEqual(2, reads); Assert.IsTrue(error.MutationAttempted);
        }

        [TestMethod] public void OperationOrdersMeasurementsAroundOneMutation()
        {
            var order = new List<string>(); int reads = 0; var feature = new object();
            var outcome = FeatureMaterialOperation.Run(MaterialChangeKind.Add,
                () => { order.Add("measure"); return ++reads == 1 ? State() : State(1e-5); },
                () => { order.Add("create"); return feature; }, _ => order.Add("inspect"));
            CollectionAssert.AreEqual(new[] { "measure", "create", "inspect", "measure" }, order);
            Assert.AreSame(feature, outcome.Feature); Assert.AreEqual(10000.0, outcome.Evidence.VolumeDeltaMm3, 1e-8);
        }

        [TestMethod] public void ResultReadbackFailureRetainsPartialStateAndBothMeasurements()
        {
            int reads = 0;
            var error = Assert.ThrowsException<FeatureMaterialVerificationException>(() => FeatureMaterialOperation.Run(
                MaterialChangeKind.Add, () => ++reads == 1 ? State() : State(1e-5), () => new object(), _ => { },
                (feature, evidence) => throw new InvalidOperationException("feature name/serialization readback")));
            Assert.IsTrue(error.MutationAttempted); Assert.IsNotNull(error.Before); Assert.IsNotNull(error.After);
            Assert.AreEqual(10000.0, error.After.VolumeMm3, 1e-8);
        }

        private static SolidMaterialSnapshot State(params double[] volumes) =>
            SolidMaterialSnapshot.FromBodyVolumesMetresCubed(volumes);
    }
}
