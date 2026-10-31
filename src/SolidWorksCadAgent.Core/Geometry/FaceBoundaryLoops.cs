using System;
using System.Collections.Generic;

namespace SolidWorksCadAgent.Core.Geometry
{
    public sealed class FaceBoundaryLoop
    {
        public string Key { get; set; }
        public bool IsOuter { get; set; }
        public bool IsSingular { get; set; }
        public IReadOnlyList<string> EdgeKeys { get; set; }
    }
    public static class FaceBoundaryLoops
    {
        public static FaceBoundaryLoop Outer(IReadOnlyList<FaceBoundaryLoop> loops)
        {
            if(loops==null||loops.Count==0||loops.Count>4096)throw new TopologyQueryException("Face loops are absent or exceed the 4096-loop budget.");
            var loopIds=new HashSet<string>(StringComparer.Ordinal);var edgeIds=new HashSet<string>(StringComparer.Ordinal);
            FaceBoundaryLoop outer=null;int visits=0;
            foreach(var loop in loops)
            {
                if(loop==null||string.IsNullOrWhiteSpace(loop.Key)||loop.Key.Length>256||!loopIds.Add(loop.Key)||loop.IsSingular||loop.EdgeKeys==null||loop.EdgeKeys.Count==0)
                    throw new TopologyQueryException("Face loop evidence is singular, incomplete or duplicated.");
                if(loop.EdgeKeys.Count>20000-visits)throw new TopologyQueryException("Face loops exceed the 20000-edge-visit budget.");
                visits+=loop.EdgeKeys.Count;
                foreach(string edge in loop.EdgeKeys)
                    if(string.IsNullOrWhiteSpace(edge)||edge.Length>256||!edgeIds.Add(edge))throw new TopologyQueryException("Face boundary edge identity is absent or repeated.");
                if(!loop.IsOuter)continue;
                if(outer!=null)throw new TopologyQueryException("Multiple outer loops are ambiguous.");
                if(loop.EdgeKeys.Count>256)throw new TopologyQueryException("Outer boundary exceeds the 256-edge budget.");
                outer=loop;
            }
            return outer??throw new TopologyQueryException("No outer face loop is identified.");
        }
    }
}
