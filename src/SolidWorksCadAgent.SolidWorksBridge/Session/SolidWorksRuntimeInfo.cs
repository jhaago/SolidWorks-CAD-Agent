namespace SolidWorksCadAgent.SolidWorksBridge.Session
{
    public sealed class SolidWorksRuntimeInfo
    {
        public string RevisionNumber { get; set; }
        public int MajorRevision { get; set; }
        public int ReleaseYear { get; set; }
        public int ServicePack { get; set; }
        public int ServicePackHotfix { get; set; }
        public string DisplayVersion { get; set; }
    }
}
