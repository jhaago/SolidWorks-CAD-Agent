using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SolidWorksCadAgent.Contracts.Design;
namespace SolidWorksCadAgent.AgentHost.Design
{
    public sealed class ReferenceImageStore
    {
        public const int MaxImageBytes = 4 * 1024 * 1024;
        private readonly string workspace;

        public ReferenceImageStore(string workspacePath)
        {
            workspace = Path.GetFullPath(workspacePath);
            CheckAncestors(workspace);
        }

        private static void Fail(string code, string message)
        {
            throw new DesignIntakeException(code, message);
        }

        private static void CheckAncestors(string path)
        {
            for (var p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            {
                if ((Directory.Exists(p) || File.Exists(p)) &&
                    (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    Fail("unsafe_path", "Reference storage cannot use linked directories.");
            }
        }

        private string Resolve(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(workspace, relative));
            if (Path.IsPathRooted(relative) || !path.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Fail("unsafe_path", "Invalid reference path.");
            CheckAncestors(path);
            return path;
        }

        private static string Hash(byte[] bytes)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string Validate(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaxImageBytes) Fail("image_size", "Each image must contain at most 4 MiB.");
            bool png = bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            bool jpg = bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
            if (!png && !jpg) Fail("invalid_image", "Only valid PNG and JPEG images are supported.");
            try
            {
                using (var s = new MemoryStream(bytes, false))
                using (var image = Image.FromStream(s, true, true))
                {
                    if ((long)image.Width * image.Height > 16000000) Fail("image_dimensions", "Images must contain at most 16 million pixels.");
                    if (!(png && image.RawFormat.Guid == ImageFormat.Png.Guid) && !(jpg && image.RawFormat.Guid == ImageFormat.Jpeg.Guid)) Fail("invalid_image", "Image format does not match its signature.");
                    using (var bitmap = new Bitmap(image))
                    {
                        bitmap.GetPixel(0, 0);
                    }
                }
            }
            catch (DesignIntakeException)
            {
                throw;
            }
            catch
            {
                Fail("invalid_image", "The uploaded image could not be decoded.");
            }
            return png ? "image/png" : "image/jpeg";
        }

        public DesignReference Save(Guid sessionId, string filename, byte[] bytes, string label, string viewType)
        {
            string media = Validate(bytes);
            var extension = Path.GetExtension(filename ?? "").ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg") Fail("unsupported_image_type", "Use a PNG or JPEG filename.");
            if ((media == "image/png") != (extension == ".png")) Fail("invalid_image", "Image bytes do not match the filename extension.");
            var id = Guid.NewGuid();
            var relative = Path.Combine(".design-intake", sessionId.ToString("N"), id.ToString("N") + (media == "image/png" ? ".png" : ".jpg"));
            var path = Resolve(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            CheckAncestors(path);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length);
            var basename = Path.GetFileName((filename ?? "reference").Replace('\\', '/'));
            basename = new string(basename.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).Take(160).ToArray());
            return new DesignReference
            {
                Id = id,
                FileName = string.IsNullOrWhiteSpace(basename) ? "reference" : basename,
                RelativePath = relative,
                MediaType = media,
                Label = label ?? "",
                ViewType = viewType ?? "",
                AddedUtc = DateTime.UtcNow,
                Sha256 = Hash(bytes),
                ByteLength = bytes.Length
            };
        }

        public byte[] Read(DesignReference reference)
        {
            var path = Resolve(reference.RelativePath);
            if (!File.Exists(path))
            {
                Fail("missing_image", "A reference image is missing. Upload it again.");
            }
            var info = new FileInfo(path);
            if (info.Length != reference.ByteLength || info.Length > MaxImageBytes) Fail("image_integrity", "A reference image has changed.");
            byte[] bytes = File.ReadAllBytes(path);
            if (Hash(bytes) != reference.Sha256 || Validate(bytes) != reference.MediaType) Fail("image_integrity", "A reference image has changed.");
            return bytes;
        }
    }
}



