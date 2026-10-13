using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.Workspace;
using SolidWorksCadAgent.SolidWorksBridge;
using SolidWorksCadAgent.SolidWorksBridge.Session;
using SolidWorksCadAgent.SolidWorksBridge.Parametric;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif
namespace SolidWorksCadAgent.IntegrationTests
{
    [TestClass,DoNotParallelize,TestCategory("SolidWorksIntegration")]
    public class ParametricPlateNativeRecipeTests
    {
        [DataTestMethod,DataRow(false),DataRow(true),Timeout(180000)]
        public async Task AsymmetricRectangleIsFullyDefinedAndWidthAndThicknessDriven(bool changeWidth)
        {
            if(System.Environment.GetEnvironmentVariable("SOLIDWORKS_RUN_REFERENCE_TESTS")!="1") Assert.Inconclusive("Native opt-in required.");
#if SOLIDWORKS_INTEROP
            using(var session=new SolidWorksSession())
            using(var bridge=new SolidWorksBridgeFacade(session,new WorkspacePolicy(Path.Combine(Path.GetTempPath(),"parametric-recipe-"+Guid.NewGuid().ToString("N")))))
            {
                var status=await session.AttachAsync(CancellationToken.None);Assert.IsTrue(status.IsConnected,status.ErrorMessage);
                var original=await session.InvokeWithApplicationAsync(a=>((SldWorks)a).IActiveDoc2?.GetTitle(),CancellationToken.None);
                string owned=null;
                byte[] sketchOwnerBytes=null;
                try
                {
                    var created=await bridge.ExecuteAsync(new CadCommandEnvelope {Command="NewPart",Parameters=new JObject()},CancellationToken.None);Assert.IsTrue(created.Success,created.Error?.Message);
                    owned=await session.InvokeWithApplicationAsync(a=>((SldWorks)a).IActiveDoc2.GetTitle(),CancellationToken.None);Assert.AreNotEqual(original,owned);
                    var sketch=await bridge.ExecuteAsync(new CadCommandEnvelope {Command="CreateSketch",Parameters=JObject.Parse("{plane:'Top Plane'}")},CancellationToken.None);Assert.IsTrue(sketch.Success,sketch.Error?.Message);
                    await session.InvokeWithApplicationAsync(a=>
                    {
                        var app=(SldWorks)a;var model=app.IActiveDoc2;
                        bool preference=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
                        var observed=ParametricRectangleBuilder.Build(app,model,Guid.NewGuid(),Guid.NewGuid(),120,60);
                        Assert.IsTrue(observed.FullyDefined,"Native sketch must be fully defined.");
                        Assert.IsTrue(observed.RequiredRelationsPresent);Assert.IsFalse(observed.HasFixedContourEntities);
                        Assert.AreEqual(4,observed.ContourEdgeCount);Assert.AreEqual(1,observed.ConstructionEdgeCount);
                        Assert.AreEqual(120.0,observed.Dimensions.Single(d=>d.Role=="width").ValueMm,0.001);
                        Assert.AreEqual(60.0,observed.Dimensions.Single(d=>d.Role=="length").ValueMm,0.001);
                        Assert.IsTrue(observed.Dimensions.All(d=>d.IsDriving));
                        Assert.AreEqual(preference,app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate));
                        var role=observed.Roles.Single(r=>r.Role=="width");
                        sketchOwnerBytes=role.OwnerReferenceBytes;
                        int referenceStatus;
                        var owner=(Feature)model.Extension.GetObjectByPersistReference3(role.OwnerReferenceBytes,out referenceStatus);
                        Assert.AreEqual(0,referenceStatus);
                        Dimension dimension=null;
                        for(var display=(DisplayDimension)owner.GetFirstDisplayDimension();display!=null;display=(DisplayDimension)owner.GetNextDisplayDimension(display))
                        {
                            var candidate=(Dimension)display.GetDimension2(0);
                            if(candidate.FullName==role.NativeDimensionFullName)dimension=candidate;
                        }
                        Assert.IsNotNull(dimension,"Captured width must resolve on the captured native owner.");
                        if(changeWidth)Assert.AreEqual(0,dimension.SetSystemValue3(0.150,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));
                        Assert.IsTrue(model.ForceRebuild3(false));
                        var rebuiltSketch=(Sketch)owner.GetSpecificFeature2();
                        var segments=((object[])rebuiltSketch.GetSketchSegments()).Cast<SketchSegment>().Where(s=>!s.ConstructionGeometry).Cast<SketchLine>().ToArray();
                        var xs=segments.SelectMany(l=>new[]{((SketchPoint)l.GetStartPoint2()).X,((SketchPoint)l.GetEndPoint2()).X}).ToArray();
                        var ys=segments.SelectMany(l=>new[]{((SketchPoint)l.GetStartPoint2()).Y,((SketchPoint)l.GetEndPoint2()).Y}).ToArray();
                        Assert.AreEqual(changeWidth?-0.075:-0.060,xs.Min(),0.000001);Assert.AreEqual(changeWidth?0.075:0.060,xs.Max(),0.000001);
                        Assert.AreEqual(-0.030,ys.Min(),0.000001);Assert.AreEqual(0.030,ys.Max(),0.000001);
                        return true;
                    },CancellationToken.None);
                    await session.InvokeWithApplicationAsync(a=>
                    {
                        var model=((SldWorks)a).IActiveDoc2;int nativeStatus;
                        var feature=(Feature)model.Extension.GetObjectByPersistReference3(sketchOwnerBytes,out nativeStatus);
                        Assert.AreEqual(0,nativeStatus);model.ClearSelection2(true);Assert.IsTrue(feature.Select2(false,0));return true;
                    },CancellationToken.None);
                    var extrude=await bridge.ExecuteAsync(new CadCommandEnvelope {Command="Extrude",Parameters=JObject.Parse("{depthMm:10}")},CancellationToken.None);
                    Assert.IsTrue(extrude.Success,extrude.Error?.Message);
                    await session.InvokeWithApplicationAsync(a=>
                    {
                        var model=((SldWorks)a).IActiveDoc2;
                        Feature feature=null;
                        for(var candidate=(Feature)model.FirstFeature();candidate!=null;candidate=(Feature)candidate.GetNextFeature())
                            if(candidate.Name==extrude.Data.Value<string>("featureName"))feature=candidate;
                        Assert.IsNotNull(feature);
                        var capture=ParametricExtrudeBuilder.Capture(model,feature,Guid.NewGuid(),Guid.NewGuid(),10);
                        var role=capture.Roles.Single(r=>r.Role=="thickness");
                        Assert.AreEqual(10.0,capture.Dimensions.Single().ValueMm,0.001);Assert.IsTrue(capture.Dimensions.Single().IsDriving);
                        Dimension dimension=null;
                        for(var display=(DisplayDimension)feature.GetFirstDisplayDimension();display!=null;display=(DisplayDimension)feature.GetNextDisplayDimension(display))
                        {var candidate=(Dimension)display.GetDimension2(0);if(candidate.FullName==role.NativeDimensionFullName)dimension=candidate;}
                        Assert.IsNotNull(dimension);
                        Assert.AreEqual(0,dimension.SetSystemValue3(0.012,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null));
                        Assert.IsTrue(model.ForceRebuild3(false));
                        var bodies=(object[])((PartDoc)model).GetBodies2((int)swBodyType_e.swSolidBody,false);Assert.AreEqual(1,bodies.Length);
                        var body=(Body2)bodies[0];var mass=(double[])body.GetMassProperties(1);
                        Assert.AreEqual(changeWidth?108000.0:86400.0,mass[3]*1e9,0.01);
                        double x,y,z;body.GetExtremePoint(0,1,0,out x,out y,out z);Assert.AreEqual(0.012,y,0.000001);
                        body.GetExtremePoint(1,0,0,out x,out y,out z);Assert.AreEqual(changeWidth?0.075:0.060,x,0.000001);
                        return true;
                    },CancellationToken.None);
                }
                finally
                {
                    await session.InvokeWithApplicationAsync(a=>
                    {
                        var app=(SldWorks)a;
                        if(owned!=null && owned!=original)app.CloseDoc(owned);
                        if(original!=null){int errors=0;app.ActivateDoc3(original,false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref errors);}
                        return true;
                    },CancellationToken.None);
                }
            }
#else
            Assert.Inconclusive("Native interop required.");
#endif
        }
    }
}
