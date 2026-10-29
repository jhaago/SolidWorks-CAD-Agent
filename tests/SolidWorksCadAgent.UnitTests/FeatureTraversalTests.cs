using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.SolidWorksBridge.Inspection;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FeatureTraversalTests
    {
        [TestMethod] public void VisitsAbsorbedSketchAndNestedErrorsInTreeOrder()
        {
            var sketch = new Node("Sketch", new Node("Relation"));
            var entries = Walk(new Node("Boss", sketch), new Node("Cut"));
            CollectionAssert.AreEqual(new[] { "Boss", "Sketch", "Relation", "Cut" }, entries.Select(e => e.Node.Id).ToArray());
            Assert.AreEqual(2, entries[2].Depth); Assert.AreEqual("Sketch", entries[2].Parent.Id);
            Assert.IsNull(entries[0].Parent);
        }

        [TestMethod] public void SharedSubfeatureIsEmittedOnce()
        {
            var shared = new Node("Shared");
            Assert.AreEqual(3, Walk(new Node("Boss", shared), new Node("Cut", shared)).Count);
        }

        [TestMethod] public void ChildLaterReturnedAtTopLevelIsNotDuplicated()
        {
            var child = new Node("Sketch"); Assert.AreEqual(2, Walk(new Node("Boss", child), child).Count);
        }

        [TestMethod] public void AncestorCycleFailsInspection()
        {
            var node = new Node("Boss"); node.Children.Add(node);
            Assert.ThrowsException<InvalidOperationException>(() => Walk(node));
        }

        [TestMethod] public void IndirectCycleFailsInspection()
        {
            var a = new Node("A"); var b = new Node("B", a); a.Children.Add(b);
            Assert.ThrowsException<InvalidOperationException>(() => Walk(a));
        }

        [TestMethod] public void DepthLimitFailsInsteadOfTruncating()
        {
            var root = new Node("A", new Node("B", new Node("C")));
            Assert.ThrowsException<InvalidOperationException>(() => BoundedFeatureTraversal.Walk(new[] { root }, n => n.Id, n => n.Children, 10, 1));
        }

        [TestMethod] public void DuplicateSiblingCycleFailsInspection()
        {
            var root = new Node("A");
            Assert.ThrowsException<InvalidOperationException>(() => BoundedFeatureTraversal.Walk(Repeat(root), n => n.Id, n => n.Children, 3));
        }

        [TestMethod] public void UnboundedUniqueEnumerationHitsVisitLimit() =>
            Assert.ThrowsException<InvalidOperationException>(() => BoundedFeatureTraversal.Walk(Unique(), n => n.Id, n => n.Children, 3));

        [TestMethod] public void NodeLimitIncludesLastUniqueNode()
        {
            Assert.AreEqual(2, BoundedFeatureTraversal.Walk(new[] { new Node("A", new Node("B")) }, n => n.Id, n => n.Children, 2).Count);
        }

        [TestMethod] public void MissingIdentityFailsInspection() =>
            Assert.ThrowsException<InvalidOperationException>(() => Walk(new Node(null)));

        [TestMethod] public void NullNodeFailsInspection() =>
            Assert.ThrowsException<InvalidOperationException>(() => Walk(new Node[] { null }));

        [TestMethod] public void EmptyPartHasAnEmptyFeatureList() => Assert.AreEqual(0, Walk().Count);

        [TestMethod] public void ChildReadFailureDoesNotReturnPartialSuccess() =>
            Assert.ThrowsException<InvalidOperationException>(() => BoundedFeatureTraversal.Walk(new[] { new Node("A") }, n => n.Id,
                n => throw new InvalidOperationException("native read")));

        private static IReadOnlyList<FeatureTreeEntry<Node>> Walk(params Node[] roots) =>
            BoundedFeatureTraversal.Walk(roots, n => n.Id, n => n.Children);
        private static IEnumerable<Node> Repeat(Node node) { while (true) yield return node; }
        private static IEnumerable<Node> Unique() { for (int i = 0; ; i++) yield return new Node(i.ToString()); }
        private sealed class Node
        {
            public Node(string id, params Node[] children) { Id = id; Children = children.ToList(); }
            public string Id { get; }
            public List<Node> Children { get; }
        }
    }
}
