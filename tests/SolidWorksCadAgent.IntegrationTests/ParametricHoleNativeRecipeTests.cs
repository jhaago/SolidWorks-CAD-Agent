using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Parametric;
using SolidWorksCadAgent.SolidWorksBridge.Session;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass,DoNotParallelize,TestCategory("SolidWorksIntegration")]
    public class ParametricHoleNativeRecipeTests
    {
        [DataTestMethod,DataRow(false,false),DataRow(true,false),DataRow(false,true),Timeout(180000)]
        public async Task CircleIsFullyDefinedWithIndependentDrivingDiameterAndAxes(bool equal,bool disabledSolver)
        {
            if(System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS")!="1")Assert.Inconclusive("Native opt-in required.");
#if SOLIDWORKS_INTEROP
            using(var session=new SolidWorksSession())
            using(var bridge=new SolidWorksBridgeFacade(session,new WorkspacePolicy(Path.Combine(Path.GetTempPath(),"hole-recipe-"+Guid.NewGuid().ToString("N")))))
            {
                var status=await session.AttachAsync(CancellationToken.None);Assert.IsTrue(status.IsConnected,status.ErrorMessage);
                var original=await session.InvokeWithApplicationAsync(a=>((SldWorks)a).IActiveDoc2?.GetTitle(),CancellationToken.None);string owned=null;
                try
                {
                    var created=await bridge.ExecuteAsync(new CadCommandEnvelope{Command="NewPart",Parameters=new JObject()},CancellationToken.None);Assert.IsTrue(created.Success,created.Error?.Message);
                    owned=await session.InvokeWithApplicationAsync(a=>((SldWorks)a).IActiveDoc2.GetTitle(),CancellationToken.None);Assert.AreNotEqual(original,owned);
                    var sketch=await bridge.ExecuteAsync(new CadCommandEnvelope{Command="CreateSketch",Parameters=JObject.Parse("{plane:'Top Plane'}")},CancellationToken.None);Assert.IsTrue(sketch.Success,sketch.Error?.Message);
                    await session.InvokeWithApplicationAsync(a=>
                    {
                        var app=(SldWorks)a;var doc=app.IActiveDoc2;var manager=doc.SketchManager;
                        bool pref=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate),add=manager.AddToDB,display=manager.DisplayWhenAdded,solve=manager.AutoSolve;
                        var modelId=Guid.NewGuid();var sketchId=Guid.NewGuid();
                        try
                        {
                            if(disabledSolver)
                            {
                                manager.AutoSolve=false;
                                Assert.ThrowsException<InvalidOperationException>(()=>ParametricHoleBuilder.Build(app,doc,modelId,sketchId,8,20,10));
                                return true;
                            }
                            var capture=ParametricHoleBuilder.Build(app,doc,modelId,sketchId,equal?20:8,20,equal?20:10);
                            Assert.IsNotNull(manager.ActiveSketch,"Leave the owned sketch active for explicit journalled ExitSketch.");
                            Assert.IsTrue(capture.FullyDefined);Assert.IsTrue(capture.RequiredRelationsPresent);Assert.IsFalse(capture.HasFixedContourEntities);
                            Assert.AreEqual(1,capture.ContourEdgeCount);Assert.AreEqual(0,capture.ConstructionEdgeCount);Assert.AreEqual(3,capture.Dimensions.Count);
                            Assert.IsTrue(capture.Dimensions.All(d=>d.IsDriving));
                            var identity=new CadModelIdentityRecord{ModelId=modelId,ConfigurationKey=capture.ConfigurationKey};
                            var bindings=Bindings(capture);
                            var observation=ParametricHoleInspector.Inspect(doc,identity,sketchId,bindings);
                            Assert.AreEqual(equal?20.0:8.0,observation.Geometry.Value<double>("diameterMm"),0.001);
                            int nativeStatus;var owner=(Feature)doc.Extension.GetObjectByPersistReference3(capture.Roles.Single(r=>r.Role=="holeX").OwnerReferenceBytes,out nativeStatus);Assert.AreEqual(0,nativeStatus);
                            var x=(Dimension)doc.Parameter("AgentHoleX@"+owner.Name);var y=(Dimension)doc.Parameter("AgentHoleY@"+owner.Name);
                            Assert.IsNotNull(x);Assert.IsNotNull(y);
                            if(equal)
                            {
                                x.Name="TemporaryHoleX";y.Name="AgentHoleX";x.Name="AgentHoleY";
                                Assert.ThrowsException<InvalidOperationException>(()=>ParametricHoleInspector.Inspect(doc,identity,sketchId,bindings),"Equal values cannot establish axis semantics.");
                            }
                            else
                            {
                                var diameter=(Dimension)doc.Parameter("AgentHoleDiameter@"+owner.Name);Assert.IsNotNull(diameter);
                                Assert.AreEqual(0,diameter.SetSystemValue3(0.012,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));
                                Assert.AreEqual(0,x.SetSystemValue3(0.025,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));
                                Assert.AreEqual(0,y.SetSystemValue3(0.012,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));
                                Assert.IsTrue(doc.ForceRebuild3(false));
                                observation=ParametricHoleInspector.Inspect(doc,identity,sketchId,bindings);
                                Assert.IsTrue(observation.FullyDefined);Assert.AreEqual(12.0,observation.Geometry.Value<double>("diameterMm"),0.001);
                                Assert.AreEqual(25.0,observation.Geometry.Value<double>("centerXmm"),0.001);Assert.AreEqual(12.0,observation.Geometry.Value<double>("centerYmm"),0.001);
                            }
                            return true;
                        }
                        finally
                        {
                            Assert.AreEqual(pref,app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate));
                            Assert.AreEqual(add,manager.AddToDB);Assert.AreEqual(display,manager.DisplayWhenAdded);
                            Assert.AreEqual(0,((SelectionMgr)doc.SelectionManager).GetSelectedObjectCount2(-1));manager.AutoSolve=solve;
                        }
                    },CancellationToken.None);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(a=>{var app=(SldWorks)a;if(owned!=null&&owned!=original)app.CloseDoc(owned);
                        if(original!=null){int errors=0;app.ActivateDoc3(original,false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref errors);}return true;},CancellationToken.None);
                }
            }
#else
            Assert.Inconclusive("Native interop required.");
#endif
        }
        private static List<CadEntityReferenceBinding> Bindings(CadParametricHoleObservation capture)
        {
            var bindings=new List<CadEntityReferenceBinding>{new CadEntityReferenceBinding{ModelId=capture.ModelId,EntityId=capture.SketchEntityId,
                ConfigurationKey=capture.ConfigurationKey,Status=CadEntityReferenceStatus.Active,NativeReferenceBytes=capture.Roles.First(r=>r.OwnerKind=="DimensionOwnerFeature").OwnerReferenceBytes}};
            foreach(var role in capture.Roles)bindings.Add(new CadEntityReferenceBinding{ModelId=capture.ModelId,EntityId=role.EntityId,ConfigurationKey=capture.ConfigurationKey,
                Status=CadEntityReferenceStatus.Active,ReferenceFormatVersion=3,NativeReferenceBytes=role.OwnerReferenceBytes,NativeObjectKind=role.OwnerKind,
                SemanticFingerprintJson=new JObject{["evidenceVersion"]=1,["modelId"]=capture.ModelId.ToString("D"),["sketchEntityId"]=capture.SketchEntityId.ToString("D"),
                    ["configurationKey"]=capture.ConfigurationKey,["role"]=role.Role,["ownerEntityId"]=capture.SketchEntityId.ToString("D"),
                    ["nativeDimensionName"]=role.NativeDimensionFullName?.Split('@')[0]}.ToString()});return bindings;
        }
    }
}
