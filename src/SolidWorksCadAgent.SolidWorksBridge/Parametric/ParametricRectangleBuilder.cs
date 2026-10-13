using System;
using System.Linq;
using System.Runtime.InteropServices;
using SolidWorksCadAgent.Contracts.Cad;
using SolidWorksCadAgent.Core.References;
#if SOLIDWORKS_INTEROP
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
#endif

namespace SolidWorksCadAgent.SolidWorksBridge.Parametric
{
    /// <summary>Called only inside the session STA on the owned active sketch.</summary>
    public static class ParametricRectangleBuilder
    {
        public static CadParametricPlateObservation Build(object application,object document,Guid modelId,Guid sketchEntityId,double widthMm,double lengthMm)
        {
            if(modelId==Guid.Empty||sketchEntityId==Guid.Empty||!Size(widthMm)||!Size(lengthMm))throw new ArgumentException("Trusted identity and representable dimensions required.");
#if SOLIDWORKS_INTEROP
            var model=(ModelDoc2)document;var manager=model.SketchManager;var sketch=manager.ActiveSketch;
            if(sketch==null || ((object[])sketch.GetSketchSegments())?.Length>0)throw new InvalidOperationException("An empty active owned sketch is required.");
            var app=(SldWorks)application;
            bool addToDb=manager.AddToDB,display=manager.DisplayWhenAdded;
            bool prompt=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            try
            {
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,false);
                manager.AddToDB=true;manager.DisplayWhenAdded=false;
                double x=widthMm/2000,y=lengthMm/2000;
                var lines=new[]{manager.CreateLine(-x,-y,0,x,-y,0),manager.CreateLine(x,-y,0,x,y,0),
                    manager.CreateLine(x,y,0,-x,y,0),manager.CreateLine(-x,y,0,-x,-y,0)};
                if(lines.Any(l=>l==null))throw new InvalidOperationException("Native rectangle line creation failed.");
                var relations=sketch.RelationManager;
                for(int i=0;i<4;i++)
                {
                    Add(relations,new object[]{lines[i]},i%2==0?swConstraintType_e.swConstraintType_HORIZONTAL:swConstraintType_e.swConstraintType_VERTICAL);
                    var end=(SketchPoint)((SketchLine)lines[i]).GetEndPoint2();
                    var start=(SketchPoint)((SketchLine)lines[(i+1)%4]).GetStartPoint2();
                    if(!Same(end,start))Add(relations,new object[]{end,start},swConstraintType_e.swConstraintType_COINCIDENT);
                }
                var diagonal=manager.CreateLine(-x,-y,0,x,y,0);if(diagonal==null)throw new InvalidOperationException("Construction diagonal failed.");
                diagonal.ConstructionGeometry=true;
                var diagonalLine=(SketchLine)diagonal;
                var dStart=(SketchPoint)diagonalLine.GetStartPoint2();var dEnd=(SketchPoint)diagonalLine.GetEndPoint2();
                var bottomLeft=(SketchPoint)((SketchLine)lines[0]).GetStartPoint2();var topRight=(SketchPoint)((SketchLine)lines[1]).GetEndPoint2();
                if(!Same(dStart,bottomLeft))Add(relations,new object[]{dStart,bottomLeft},swConstraintType_e.swConstraintType_COINCIDENT);
                if(!Same(dEnd,topRight))Add(relations,new object[]{dEnd,topRight},swConstraintType_e.swConstraintType_COINCIDENT);
                var origin=Origin(model);
                Add(relations,new object[]{origin,diagonal},swConstraintType_e.swConstraintType_ATMIDDLE);
                manager.AddToDB=false;
                model.ClearSelection2(true);if(!lines[0].Select4(false,null))throw new InvalidOperationException("Width edge selection failed.");
                var width=Dimension(model.AddDimension2(0,-y-0.01,0),"AgentWidth",widthMm);
                model.ClearSelection2(true);if(!lines[1].Select4(false,null))throw new InvalidOperationException("Length edge selection failed.");
                var length=Dimension(model.AddDimension2(x+0.01,0,0),"AgentLength",lengthMm);
                model.ClearSelection2(true);
                Feature feature=null;
                for(var candidate=(Feature)model.FirstFeature();candidate!=null;candidate=(Feature)candidate.GetNextFeature())
                {
                    if(candidate.GetTypeName2()=="ProfileFeature" && candidate.GetSpecificFeature2() is Sketch specific && Same(specific,sketch)) { feature=candidate;break; }
                }
                if(feature==null)throw new InvalidOperationException("Actual sketch feature owner could not be resolved.");
                byte[] reference=(byte[])model.Extension.GetPersistReference3(feature);
                if(reference==null||reference.Length==0)throw new InvalidOperationException("Sketch owner reference missing.");
                var result=new CadParametricPlateObservation { EvidenceVersion=1,ModelId=modelId,SketchEntityId=sketchEntityId,
                    ConfigurationKey=model.ConfigurationManager.ActiveConfiguration.Name,
                    FullyDefined=sketch.GetConstrainedStatus()==(int)swConstrainedStatus_e.swFullyConstrained,
                    ContourEdgeCount=4,ConstructionEdgeCount=1 };
                var nativeRelations=((object[])relations.GetRelations(0)??new object[0]).Cast<SketchRelation>().Select(r=>r.GetRelationType()).ToArray();
                result.RequiredRelationsPresent=nativeRelations.Count(t=>t==(int)swConstraintType_e.swConstraintType_HORIZONTAL)==2 &&
                    nativeRelations.Count(t=>t==(int)swConstraintType_e.swConstraintType_VERTICAL)==2 &&nativeRelations.Contains((int)swConstraintType_e.swConstraintType_ATMIDDLE);
                result.HasFixedContourEntities=nativeRelations.Contains((int)swConstraintType_e.swConstraintType_FIXED);
                for(int i=0;i<4;i++)result.Roles.Add(Capture(model,lines[i],modelId,sketchEntityId,new[]{"bottom","right","top","left"}[i],"SketchSegment",null));
                result.Roles.Add(Capture(model,diagonal,modelId,sketchEntityId,"centreDiagonal","SketchSegment",null));
                foreach(var pair in new[]{new{Role="width",Dim=width},new{Role="length",Dim=length}})
                {
                    result.Roles.Add(Capture(model,feature,modelId,sketchEntityId,pair.Role,"DimensionOwnerFeature",pair.Dim.FullName));
                    result.Dimensions.Add(new CadParametricDimensionObservation { EntityId=CadParametricIdentity.For(modelId,sketchEntityId,pair.Role),
                        Role=pair.Role,OwnerEntityId=sketchEntityId,ValueMm=pair.Dim.GetSystemValue2(result.ConfigurationKey)*1000,
                        IsDriving=pair.Dim.DrivenState==(int)swDimensionDrivenState_e.swDimensionDriving });
                }
                if(!result.FullyDefined||!result.RequiredRelationsPresent||result.HasFixedContourEntities||result.Dimensions.Any(d=>!d.IsDriving))
                    throw new InvalidOperationException("Native rectangle did not establish the required driving constraints; status="+sketch.GetConstrainedStatus()+", relations="+string.Join(",",nativeRelations));
                return result;
            }
            finally
            {
                model.ClearSelection2(true);manager.AddToDB=addToDb;manager.DisplayWhenAdded=display;
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,prompt);
            }
#else
            throw new NotSupportedException("Native SOLIDWORKS interop is required.");
#endif
        }
        private static bool Size(double n)=>!double.IsNaN(n)&&!double.IsInfinity(n)&&n>0.000001;
#if SOLIDWORKS_INTEROP
        private static void Add(SketchRelationManager manager,object[] entities,swConstraintType_e type)
        {
            var dispatch=entities.Select(e=>new DispatchWrapper(e)).ToArray();
            var allowed=manager.GetAllowedRelations(dispatch) as Array;
            if(allowed==null||!allowed.Cast<object>().Any(v=>Convert.ToInt32(v)==(int)type))
                throw new InvalidOperationException("Native relation is not permitted for the captured entities: "+type);
            if(manager.AddRelation(dispatch,(int)type)==null)throw new InvalidOperationException("Native relation creation failed: "+type);
        }
        private static bool Same(object a,object b)
        { var x=Marshal.GetIUnknownForObject(a);var y=Marshal.GetIUnknownForObject(b);try{return x==y;}finally{Marshal.Release(x);Marshal.Release(y);} }
        private static SketchPoint Origin(ModelDoc2 model)
        {
            for(var feature=(Feature)model.FirstFeature();feature!=null;feature=(Feature)feature.GetNextFeature())
            {
                if(feature.GetTypeName2()!="OriginProfileFeature")continue;
                var sketch=feature.GetSpecificFeature2() as Sketch;
                var points=(sketch?.GetSketchPoints2() as object[])?.Cast<SketchPoint>().ToArray();
                if(points?.Length==1)return points[0];
            }
            throw new InvalidOperationException("The actual native origin point could not be resolved.");
        }
        private static Dimension Dimension(object display,string name,double mm)
        {
            var dimension=((DisplayDimension)display)?.GetDimension2(0) as Dimension;
            if(dimension==null)throw new InvalidOperationException("Native driving dimension creation failed.");
            dimension.Name=name;
            if(dimension.DrivenState!=(int)swDimensionDrivenState_e.swDimensionDriving || dimension.SetSystemValue3(mm/1000,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)!=0)
                throw new InvalidOperationException("Native dimension is not writable/driving.");
            return dimension;
        }
        internal static CadParametricRoleCapture Capture(ModelDoc2 model,object native,Guid modelId,Guid sketchId,string role,string kind,string name)
        {
            var bytes=model.Extension.GetPersistReference3(native) as byte[];
            if(bytes==null||bytes.Length==0)throw new InvalidOperationException("Native role reference unavailable: "+role);
            return new CadParametricRoleCapture { EntityId=CadParametricIdentity.For(modelId,sketchId,role),Role=role,OwnerKind=kind,OwnerReferenceBytes=bytes,NativeDimensionFullName=name };
        }
#endif
    }
}
