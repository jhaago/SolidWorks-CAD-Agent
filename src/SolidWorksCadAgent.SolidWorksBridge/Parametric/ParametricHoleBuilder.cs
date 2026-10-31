using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Parametric
{
    /// <summary>Only invoked on the session STA for the owned empty active sketch.</summary>
    public static class ParametricHoleBuilder
    {
        public static CadParametricHoleObservation Build(object application,object document,Guid modelId,Guid sketchId,double diameterMm,double xMm,double yMm)
        {
            if(modelId==Guid.Empty||sketchId==Guid.Empty||!Finite(diameterMm)||diameterMm<=0.000002||!Finite(xMm)||!Finite(yMm)||xMm<=0.001||yMm<=0.001)
                throw new ArgumentException("Trusted hole identities and representable positive dimensions required.");
#if SOLIDWORKS_INTEROP
            var app=(SldWorks)application;var model=(ModelDoc2)document;var manager=model.SketchManager;var sketch=manager.ActiveSketch;
            if(sketch==null||(sketch.GetSketchSegments() as object[])?.Length>0)throw new InvalidOperationException("An empty active owned sketch is required.");
            bool add=manager.AddToDB,display=manager.DisplayWhenAdded,pref=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            try
            {
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,false);manager.AddToDB=true;manager.DisplayWhenAdded=false;
                var segment=manager.CreateCircleByRadius(xMm/1000,yMm/1000,0,diameterMm/2000);
                var circle=segment as SketchArc;
                if(circle==null||circle.IsCircle()!=1||segment.ConstructionGeometry)throw new InvalidOperationException("Native full circle creation failed.");
                var centre=(SketchPoint)circle.GetCenterPoint2();var origin=ParametricRectangleBuilder.Origin(model);manager.AddToDB=false;
                model.ClearSelection2(true);if(!segment.Select4(false,null))throw new InvalidOperationException("Native circle selection failed.");
                var diameter=ParametricRectangleBuilder.Dimension(model.AddDiameterDimension2((xMm+diameterMm)/1000,yMm/1000,0),"AgentHoleDiameter",diameterMm);
                model.ClearSelection2(true);if(!origin.Select4(false,null)||!centre.Select4(true,null))throw new InvalidOperationException("Native horizontal placement point selection failed.");
                var x=ParametricRectangleBuilder.Dimension(model.AddHorizontalDimension2(xMm/2000,-0.01,0),"AgentHoleX",xMm);
                model.ClearSelection2(true);if(!origin.Select4(false,null)||!centre.Select4(true,null))throw new InvalidOperationException("Native vertical placement point selection failed.");
                var y=ParametricRectangleBuilder.Dimension(model.AddVerticalDimension2(-0.01,yMm/2000,0),"AgentHoleY",yMm);
                model.ClearSelection2(true);
                Feature feature=null;
                for(var candidate=(Feature)model.FirstFeature();candidate!=null;candidate=(Feature)candidate.GetNextFeature())
                    if(candidate.GetTypeName2()=="ProfileFeature"&&candidate.GetSpecificFeature2() is Sketch specific&&ParametricPlateInspector.SameNative(model,specific,sketch)){feature=candidate;break;}
                if(feature==null)throw new InvalidOperationException("Native hole sketch feature owner unavailable.");
                var capture=new CadParametricHoleObservation{EvidenceVersion=1,ModelId=modelId,SketchEntityId=sketchId,ConfigurationKey=model.ConfigurationManager.ActiveConfiguration.Name};
                capture.Roles.Add(ParametricRectangleBuilder.Capture(model,segment,modelId,sketchId,"holeCircle","SketchSegment",null));
                foreach(var pair in new[]{new{Role="holeDiameter",Dimension=diameter},new{Role="holeX",Dimension=x},new{Role="holeY",Dimension=y}})
                {
                    capture.Roles.Add(ParametricRectangleBuilder.Capture(model,feature,modelId,sketchId,pair.Role,"DimensionOwnerFeature",pair.Dimension.FullName));
                    capture.Dimensions.Add(new CadParametricDimensionObservation{EntityId=CadParametricIdentity.For(modelId,sketchId,pair.Role),OwnerEntityId=sketchId,Role=pair.Role,
                        ValueMm=pair.Dimension.GetSystemValue2(capture.ConfigurationKey)*1000,IsDriving=pair.Dimension.DrivenState==(int)swDimensionDrivenState_e.swDimensionDriving});
                }
                var observed=ParametricHoleInspector.Inspect(model,new CadModelIdentityRecord{ModelId=modelId,ConfigurationKey=capture.ConfigurationKey},sketchId,Bindings(capture));
                capture.FullyDefined=observed.FullyDefined;capture.RequiredRelationsPresent=observed.RequiredRelationsPresent;capture.HasFixedContourEntities=observed.HasFixedContourEntities;
                capture.ContourEdgeCount=observed.ContourEdgeCount;capture.ConstructionEdgeCount=observed.ConstructionEdgeCount;capture.Geometry=observed.Geometry;
                capture.SolidBodyCount=observed.SolidBodyCount;capture.RebuildErrorCount=observed.RebuildErrorCount;
                if(!capture.FullyDefined||!capture.RequiredRelationsPresent||capture.HasFixedContourEntities||capture.Dimensions.Any(d=>!d.IsDriving))
                    throw new InvalidOperationException("Native hole has not established its required driving constraints; solve="+sketch.GetConstrainedStatus());
                return capture;
            }
            finally{model.ClearSelection2(true);manager.AddToDB=add;manager.DisplayWhenAdded=display;app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,pref);}
#else
            throw new NotSupportedException("Native SOLIDWORKS interop required.");
#endif
        }
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
#if SOLIDWORKS_INTEROP
        private static IReadOnlyList<CadEntityReferenceBinding> Bindings(CadParametricHoleObservation capture)
        {
            var bindings=new List<CadEntityReferenceBinding>{new CadEntityReferenceBinding{ModelId=capture.ModelId,EntityId=capture.SketchEntityId,
                ConfigurationKey=capture.ConfigurationKey,Status=CadEntityReferenceStatus.Active,NativeReferenceBytes=capture.Roles.First(r=>r.OwnerKind=="DimensionOwnerFeature").OwnerReferenceBytes}};
            foreach(var role in capture.Roles)bindings.Add(new CadEntityReferenceBinding{ModelId=capture.ModelId,EntityId=role.EntityId,ConfigurationKey=capture.ConfigurationKey,
                Status=CadEntityReferenceStatus.Active,ReferenceFormatVersion=3,NativeReferenceBytes=role.OwnerReferenceBytes,NativeObjectKind=role.OwnerKind,
                SemanticFingerprintJson=new Newtonsoft.Json.Linq.JObject{["evidenceVersion"]=1,["modelId"]=capture.ModelId.ToString("D"),["sketchEntityId"]=capture.SketchEntityId.ToString("D"),
                    ["configurationKey"]=capture.ConfigurationKey,["role"]=role.Role,["ownerEntityId"]=capture.SketchEntityId.ToString("D"),
                    ["nativeDimensionName"]=role.NativeDimensionFullName?.Split('@')[0]}.ToString()});return bindings;
        }
#endif
    }
}
