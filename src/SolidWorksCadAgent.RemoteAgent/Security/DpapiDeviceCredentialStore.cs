using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SolidWorksCadAgent.Core.Remote;

namespace SolidWorksCadAgent.RemoteAgent.Security
{
    public sealed class DpapiDeviceCredentialStore : IRemoteCredentialStore
    {
        private readonly string path;
        public DpapiDeviceCredentialStore(string path) { this.path=Path.GetFullPath(path); }
        public IReadOnlyList<RemoteCredentialRecord> Load() {
            if(!File.Exists(path)) return new List<RemoteCredentialRecord>();
            if(new FileInfo(path).Length>65536) throw new CryptographicException("Remote credential storage is invalid.");
            try {
                var bytes=ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser);
                return JsonConvert.DeserializeObject<List<RemoteCredentialRecord>>(Encoding.UTF8.GetString(bytes)) ?? throw new CryptographicException("Remote credential storage is invalid.");
            } catch(Exception ex) when(ex is JsonException || ex is CryptographicException) {
                throw new CryptographicException("Remote credential storage is invalid.");
            }
        }
        public void Save(IReadOnlyList<RemoteCredentialRecord> records) {
            if(records==null || records.Count>10) throw new InvalidOperationException("Remote device storage limit exceeded.");
            var bytes=ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(records)),null,DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(bytes,0,bytes.Length); stream.Flush(true); }
                if(File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            } finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
