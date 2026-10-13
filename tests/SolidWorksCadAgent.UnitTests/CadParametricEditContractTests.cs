using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.AgentHost.Planning;
using SolidWorksCadAgent.Core.Commands;

namespace SolidWorksCadAgent.UnitTests
{
    [TestClass]
    public class CadParametricEditContractTests
    {
        internal static CadParametricEditSource Source(bool hole = true)
        {
            var json = JsonConvert.SerializeObject(hole ? CadParametricHoleContractTests.Hole() : CadParametricPlateContractTests.Plate());
            using (var sha = SHA256.Create())
                return new CadParametricEditSource {
                    JobId = Guid.NewGuid(), RevisionId = Guid.NewGuid(), ModelId = Guid.NewGuid(),
                    ModelRevisionId = Guid.NewGuid(), ConfigurationKey = "Default",
                    Path = "parametric/source.sldprt", Sha256 = new string('a',64), SourcePlanJson = json,
                    PlanSha256 = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", "").ToLowerInvariant()
                };
        }
        internal static CadPlanV2Document Edit(string role = "width", double value = 150) =>
            CadParametricEditContract.Build(Source(), role, value, "edits/result.sldprt");

        [TestMethod] public void EachRoleChangesOnlyOneExpectedParameterAndPreservesSource()
        {
            var roles = new[]{"width","length","thickness","holeDiameter","holeX","holeY"};
            var values = new[]{150.0,80.0,12.0,12.0,25.0,12.0};
            for(var index=0;index<roles.Length;index++) {
                var source=Source(); var before=source.SourcePlanJson;
                var plan=CadParametricEditContract.Build(source,roles[index],values[index],"edits/result.sldprt");
                Assert.AreEqual(0,CadParametricEditContract.Validate(plan).Count);
                var expected=CadParametricEditContract.ExpectedPart(plan);
                Assert.AreEqual(before,source.SourcePlanJson);
                var original=CadPlanDocumentReader.ReadPersisted(before).CandidateV2;
                Assert.AreEqual(original.Steps[1].OutputEntityId,expected.Steps[1].OutputEntityId);
                Assert.AreEqual(original.Steps[5].OutputEntityId,expected.Steps[5].OutputEntityId);
                Assert.AreEqual("edits/result.sldprt",expected.Steps.Last().Parameters.Value<string>("path"));
                var differences=0;
                for(var step=0;step<original.Steps.Count-1;step++)
                    foreach(var field in original.Steps[step].Parameters.Properties())
                        if(!JToken.DeepEquals(field.Value,expected.Steps[step].Parameters[field.Name])) differences++;
                Assert.AreEqual(1,differences,roles[index]);
                CollectionAssert.AreEqual(new[]{"ForkParametricPart","SetDrivingParameter","SavePart"},plan.Steps.Select(s=>s.Command).ToArray());
                Assert.IsTrue(CadPlanDocumentReader.ReadPersisted(JsonConvert.SerializeObject(plan)).IsValid);
                Assert.IsFalse(CadPlanDocumentReader.ReadCandidate(JsonConvert.SerializeObject(plan)).IsValid,"Provider candidates cannot assign verified source identity.");
            }
        }
        [TestMethod] public void RejectsUnsupportedRolesAndUnsafeGeometry()
        {
            Assert.ThrowsException<ArgumentException>(()=>CadParametricEditContract.Build(Source(false),"holeX",20,"edit.sldprt"));
            foreach(var role in new[]{"radius","Width","","arbitrary"})
                Assert.ThrowsException<ArgumentException>(()=>CadParametricEditContract.Build(Source(),role,150,"edit.sldprt"));
            foreach(var value in new[]{0.0,-1.0,double.NaN,double.PositiveInfinity,1e308})
                Assert.ThrowsException<ArgumentException>(()=>Edit("width",value));
            Assert.ThrowsException<ArgumentException>(()=>Edit("width",40));
            Assert.ThrowsException<ArgumentException>(()=>Edit("holeX",56));
            Assert.AreEqual(0,CadParametricEditContract.Validate(Edit("holeX",55.999)).Count);
            Assert.ThrowsException<ArgumentException>(()=>CadParametricEditContract.Build(Source(),"width",150,"../bad.sldprt"));
        }
        [TestMethod] public void SourceTamperingAndRecursiveEditingAreRejected()
        {
            var edit=Edit(); edit.ParametricEdit.SourcePlanJson=edit.ParametricEdit.SourcePlanJson.Replace("120.0","121.0");
            Assert.IsTrue(CadParametricEditContract.Validate(edit).Count>0);
            edit=Edit(); edit.ParametricEdit.ModelId=Guid.Empty;
            Assert.IsTrue(CadParametricEditContract.Validate(edit).Count>0);
            edit=Edit(); edit.ParametricEdit.AdditionalProperties=new System.Collections.Generic.Dictionary<string,JToken>{{"unexpected",true}};
            Assert.IsTrue(CadParametricEditContract.Validate(edit).Count>0);
            var source=Source(); source.SourcePlanJson=JsonConvert.SerializeObject(Edit());
            using(var sha=SHA256.Create()) source.PlanSha256=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(source.SourcePlanJson))).Replace("-","").ToLowerInvariant();
            Assert.ThrowsException<ArgumentException>(()=>CadParametricEditContract.Build(source,"width",160,"edits/next.sldprt"));
        }
        [TestMethod] public void RejectsAliasedMetadataExtraStepsAndMismatchedMutation()
        {
            var root=JObject.FromObject(Edit()); root["parametricEdit"]["ModelId"]=root["parametricEdit"]["modelId"]; ((JObject)root["parametricEdit"]).Remove("modelId");
            Assert.IsFalse(CadPlanDocumentReader.ReadPersisted(root.ToString()).IsValid);
            var edit=Edit(); edit.Steps[1].Parameters["extra"]=true;
            Assert.IsTrue(CadParametricEditContract.Validate(edit).Count>0);
            edit=Edit(); edit.Steps.Insert(2,edit.Steps[1]);
            Assert.IsTrue(CadParametricEditContract.Validate(edit).Count>0);
            edit=Edit(); edit.Steps.Last().Parameters["allowOverwrite"]=true;
            Assert.IsTrue(CadParametricEditContract.Validate(edit).Count>0);
        }
        [TestMethod] public void ExplicitPromptDoesNotFallBackToAnotherFamily()
        {
            var id=Guid.NewGuid();
            var prompt="V2 parametric edit: job "+id+"; parameter holeDiameter = 12 mm; save as edits/result.sldprt";
            Assert.IsTrue(CadParametricEditPrompt.IsRequest(prompt));
            var parsed=CadParametricEditPrompt.Parse(prompt);
            Assert.AreEqual(id,parsed.JobId); Assert.AreEqual("holeDiameter",parsed.Parameter); Assert.AreEqual(12.0,parsed.ValueMm);
            Assert.AreEqual("edits/result.sldprt",parsed.OutputPath);
            Assert.ThrowsException<ArgumentException>(()=>CadParametricEditPrompt.Parse(prompt.Replace("12 mm","NaN mm")));
        }
    }
}
