using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.AgentHost.Jobs
{
    public static class SubmissionInputHash
    {
        public static string Compute(string prompt, CadPlanningImage image)
        {
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt is required.", nameof(prompt));
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            using (var sha = SHA256.Create())
            {
                writer.Write(1);
                WriteText(writer, prompt.Trim());
                writer.Write(image != null);
                if (image != null)
                {
                    WriteText(writer, image.MediaType);
                    writer.Write(sha.ComputeHash(image.Bytes));
                }
                writer.Flush();
                return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }
        private static void WriteText(BinaryWriter writer, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes);
        }
    }
}
