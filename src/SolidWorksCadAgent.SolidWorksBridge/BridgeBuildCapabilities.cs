namespace SolidWorksCadAgent.SolidWorksBridge
{
    public static class BridgeBuildCapabilities
    {
#if SOLIDWORKS_INTEROP
        public const string Capability = "NativeSolidWorksInterop";
#else
        public const string Capability = "CompileOnlyNoSolidWorksInterop";
#endif
    }
}
