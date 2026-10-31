using System;

namespace SolidWorksCadAgent.Core.Geometry
{
    public static class TopologyArrayEvidence
    {
        /// <summary>Independent native count readback must agree before completeness or uniqueness is inferred.</summary>
        public static object[] RequireComplete(object[] values,int nativeCount,int budget,string description)
        {
            if(budget<0)throw new ArgumentOutOfRangeException(nameof(budget));
            if(values==null||nativeCount<=0||nativeCount>budget||values.Length!=nativeCount)
                throw new TopologyQueryException("Native "+description+" enumeration is incomplete, empty or exceeds its budget.");
            return values;
        }
    }
}
