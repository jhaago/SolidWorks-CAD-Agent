using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SolidWorksCadAgent.Core.Geometry;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class FeatureInventoryTests
    {
        [TestMethod] public void UnchangedInventoryMatchesRegardlessOfOrder()
        {Assert.IsTrue(FeatureInventory.Compare(Map("Sketch","ProfileFeature","Boss","Boss"),Map("Boss","Boss","Sketch","ProfileFeature")).Matches);}
        [TestMethod] public void AddedRemovedAndTypeChangesAreReported()
        {
            var diff=FeatureInventory.Compare(Map("Sketch","ProfileFeature","Boss","Boss","OldCut","Cut"),Map("Sketch","ProfileFeature","Boss","Cut","Extra","ProfileFeature"));
            Assert.IsFalse(diff.Matches);CollectionAssert.AreEqual(new[]{"Extra"},diff.Added);CollectionAssert.AreEqual(new[]{"OldCut"},diff.Removed);CollectionAssert.AreEqual(new[]{"Boss"},diff.TypeChanged);
        }
        [TestMethod] public void DuplicateNativeNamesCannotBeSilentlyOverwritten()=>Assert.ThrowsException<ArgumentException>(()=>FeatureInventory.Capture(new[]{new KeyValuePair<string,string>("Sketch","ProfileFeature"),new KeyValuePair<string,string>("Sketch","ProfileFeature")}));
        [TestMethod] public void MissingInventoryIsNotAProvenMatch()=>Assert.ThrowsException<ArgumentNullException>(()=>FeatureInventory.Compare(null,Map("Boss","Boss")));
        private static IReadOnlyDictionary<string,string> Map(params string[] pairs)
        {var entries=new List<KeyValuePair<string,string>>();for(int i=0;i<pairs.Length;i+=2)entries.Add(new KeyValuePair<string,string>(pairs[i],pairs[i+1]));return FeatureInventory.Capture(entries);}
    }
}
