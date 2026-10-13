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
    public static class ParametricExtrudeBuilder
    {
        public static CadParametricPlateObservation Capture(object document,object nativeFeature,Guid modelId,Guid sketchId,double thicknessMm)
        {
#if SOLIDWORKS_INTEROP
            if(modelId==Guid.Empty||sketchId==Guid.Empty)throw new ArgumentException("Trusted identity required.");
            var model=(ModelDoc2)document;var feature=(Feature)nativeFeature;
            var configuration=model.ConfigurationManager.ActiveConfiguration.Name;
            var dimensions=new List<Dimension>();
            for(var display=(DisplayDimension)feature.GetFirstDisplayDimension();display!=null;display=(DisplayDimension)feature.GetNextDisplayDimension(display))
            {
                var dimension=(Dimension)display.GetDimension2(0);
                if(dimension.DrivenState==(int)swDimensionDrivenState_e.swDimensionDriving &&
                    Math.Abs(dimension.GetSystemValue2(configuration)*1000-thicknessMm)<=0.001)dimensions.Add(dimension);
            }
            if(dimensions.Count!=1)throw new InvalidOperationException("The actual boss has no unique driving thickness dimension.");
            var depth=dimensions.Single();depth.Name="AgentThickness";
            var result=new CadParametricPlateObservation { EvidenceVersion=1,ModelId=modelId,SketchEntityId=sketchId,ConfigurationKey=configuration };
            result.Roles.Add(ParametricRectangleBuilder.Capture(model,feature,modelId,sketchId,"boss","Feature",null));
            result.Roles.Add(ParametricRectangleBuilder.Capture(model,feature,modelId,sketchId,"thickness","DimensionOwnerFeature",depth.FullName));
            result.Dimensions.Add(new CadParametricDimensionObservation { EntityId=CadParametricIdentity.For(modelId,sketchId,"thickness"),
                Role="thickness",OwnerEntityId=CadParametricIdentity.For(modelId,sketchId,"boss"),ValueMm=depth.GetSystemValue2(configuration)*1000,IsDriving=true });
            return result;
#else
            throw new NotSupportedException("Native SOLIDWORKS interop is required.");
#endif
        }
    }
}
