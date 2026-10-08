using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SolidWorksCadAgent.Core.Security;

namespace SolidWorksCadAgent.AgentHost.Security
{
    public sealed class WindowsCredentialStore : ISecretStore
    {
        private const uint GenericCredential = 1;
        private const uint PersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        public void Set(string target, string secret)
        {
            ValidateTarget(target);
            if (secret == null) throw new ArgumentNullException(nameof(secret));

            var bytes = Encoding.Unicode.GetBytes(secret);
            var blob = IntPtr.Zero;
            try
            {
                blob = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = GenericCredential,
                    TargetName = target,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = PersistLocalMachine,
                    UserName = Environment.UserName
                };
                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not store the credential.");
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
                if (blob != IntPtr.Zero)
                {
                    for (var offset = 0; offset < bytes.Length; offset++) Marshal.WriteByte(blob, offset, 0);
                    Marshal.FreeHGlobal(blob);
                }
            }
        }

        public string Get(string target)
        {
            ValidateTarget(target);
            if (!CredRead(target, GenericCredential, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound) return null;
                throw new Win32Exception(error, "Windows Credential Manager could not read the credential.");
            }

            try
            {
                var credential = (NativeCredential)Marshal.PtrToStructure(pointer, typeof(NativeCredential));
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return string.Empty;
                return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2));
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public bool Exists(string target)
        {
            ValidateTarget(target);
            if (!CredRead(target, GenericCredential, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound) return false;
                throw new Win32Exception(error, "Windows Credential Manager could not inspect the credential.");
            }

            CredFree(pointer);
            return true;
        }

        public void Delete(string target)
        {
            ValidateTarget(target);
            if (CredDelete(target, GenericCredential, 0)) return;
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error, "Windows Credential Manager could not delete the credential.");
        }

        private static void ValidateTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("A credential target is required.", nameof(target));
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
