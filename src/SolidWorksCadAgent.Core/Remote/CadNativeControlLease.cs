using System;
using System.IO;

namespace SolidWorksCadAgent.Core.Remote
{
    /// <summary>Cross-process, crash-released exclusion of native CAD mutation and remote manual input.</summary>
    public sealed class CadNativeControlLease : IDisposable
    {
        private readonly FileStream _stream;
        private CadNativeControlLease(FileStream stream) { _stream = stream; }

        public static CadNativeControlLease TryAcquire(string path = null)
        {
            var resolved = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SolidWorksCadAgent", "cad-native-control.lock");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolved));
                var stream = new FileStream(resolved, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (File.Exists(resolved + ".recovery-required"))
                {
                    stream.Dispose();
                    return null;
                }
                return new CadNativeControlLease(stream);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public void Dispose() { _stream.Dispose(); }
    }
}
