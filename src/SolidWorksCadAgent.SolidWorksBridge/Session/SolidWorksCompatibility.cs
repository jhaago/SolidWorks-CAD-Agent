using System;

namespace SolidWorksCadAgent.SolidWorksBridge.Session
{
    public enum SolidWorksCompatibilityLevel
    {
        Unknown = 0,
        OlderUncertified = 1,
        Certified = 2,
        ForwardCompatibleUncertified = 3
    }

    public sealed class SolidWorksCompatibilityResult
    {
        public SolidWorksCompatibilityLevel Level { get; set; }
        public bool CanAttemptV1Commands { get; set; }
        public bool RequiresRegressionCertification { get; set; }
        public string Message { get; set; }
    }

    public static class SolidWorksCompatibility
    {
        public const int InitialCertifiedReleaseYear = 2020;

        public static SolidWorksCompatibilityResult Evaluate(SolidWorksRuntimeInfo runtimeInfo)
        {
            if (runtimeInfo == null || runtimeInfo.ReleaseYear <= 0)
            {
                return new SolidWorksCompatibilityResult
                {
                    Level = SolidWorksCompatibilityLevel.Unknown,
                    CanAttemptV1Commands = false,
                    RequiresRegressionCertification = true,
                    Message = "SOLIDWORKS runtime version could not be classified."
                };
            }

            if (runtimeInfo.ReleaseYear == InitialCertifiedReleaseYear)
            {
                return new SolidWorksCompatibilityResult
                {
                    Level = SolidWorksCompatibilityLevel.Certified,
                    CanAttemptV1Commands = true,
                    RequiresRegressionCertification = false,
                    Message = "SOLIDWORKS 2020 is the initial certified runtime."
                };
            }

            if (runtimeInfo.ReleaseYear > InitialCertifiedReleaseYear)
            {
                return new SolidWorksCompatibilityResult
                {
                    Level = SolidWorksCompatibilityLevel.ForwardCompatibleUncertified,
                    CanAttemptV1Commands = true,
                    RequiresRegressionCertification = true,
                    Message = "This newer SOLIDWORKS runtime can use the version-independent bridge, but requires the regression suite before it is labelled certified."
                };
            }

            return new SolidWorksCompatibilityResult
            {
                Level = SolidWorksCompatibilityLevel.OlderUncertified,
                CanAttemptV1Commands = false,
                RequiresRegressionCertification = true,
                Message = "This SOLIDWORKS release predates the certified 2020 baseline."
            };
        }
    }
}
