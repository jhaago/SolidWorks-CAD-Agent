using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace SolidWorksCadAgent.Core.Remote
{
    public sealed class RemoteCredentialRecord
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string Verifier { get; set; }
    }
    public interface IRemoteCredentialStore
    {
        IReadOnlyList<RemoteCredentialRecord> Load();
        void Save(IReadOnlyList<RemoteCredentialRecord> records);
    }
    public interface IRemoteSecretGenerator { byte[] Create(); }
    public sealed class RemoteSecretGenerator : IRemoteSecretGenerator
    {
        public byte[] Create() {
            var value = new byte[32]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(value); return value;
        }
    }
    public static class RemoteSecrets
    {
        public static string New(IRemoteSecretGenerator generator) {
            var bytes = generator.Create();
            if (bytes == null || bytes.Length != 32) throw new InvalidOperationException("Remote secret generation failed.");
            return Convert.ToBase64String(bytes);
        }
        public static string Hash(string value) {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }
        public static bool Matches(string verifier, string supplied) {
            if (string.IsNullOrEmpty(verifier) || supplied == null || supplied.Length > 256) return false;
            var actual = Hash(supplied); int difference = actual.Length ^ verifier.Length;
            for (int i = 0; i < actual.Length; i++) difference |= actual[i] ^ (i < verifier.Length ? verifier[i] : 0);
            return difference == 0;
        }
    }
}
