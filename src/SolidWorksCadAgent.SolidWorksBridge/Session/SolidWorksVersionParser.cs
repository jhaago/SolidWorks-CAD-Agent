using System;
using System.Globalization;

namespace SolidWorksCadAgent.SolidWorksBridge.Session
{
    public static class SolidWorksVersionParser
    {
        private const int SolidWorks2000MajorRevision = 8;
        private const int SolidWorks2000ReleaseYear = 2000;

        public static SolidWorksRuntimeInfo ParseRevision(string revisionNumber)
        {
            if (string.IsNullOrWhiteSpace(revisionNumber))
            {
                throw new FormatException("SOLIDWORKS revision number is empty.");
            }

            var parts = revisionNumber.Split('.');
            if (parts.Length < 2 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var servicePack))
            {
                throw new FormatException("SOLIDWORKS revision number is not in a supported major.minor[.hotfix] form.");
            }

            var hotfix = 0;
            if (parts.Length >= 3 &&
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out hotfix))
            {
                throw new FormatException("SOLIDWORKS revision hotfix component is invalid.");
            }

            if (major < SolidWorks2000MajorRevision)
            {
                throw new NotSupportedException("SOLIDWORKS releases before 2000 are outside the supported runtime mapping.");
            }

            var releaseYear = SolidWorks2000ReleaseYear + (major - SolidWorks2000MajorRevision);

            return new SolidWorksRuntimeInfo
            {
                RevisionNumber = revisionNumber,
                MajorRevision = major,
                ReleaseYear = releaseYear,
                ServicePack = servicePack,
                ServicePackHotfix = hotfix,
                DisplayVersion = string.Format(
                    CultureInfo.InvariantCulture,
                    "SOLIDWORKS {0} SP{1}.{2}",
                    releaseYear,
                    servicePack,
                    hotfix)
            };
        }
    }
}
