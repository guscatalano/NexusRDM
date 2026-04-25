using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NexusRDM.Core.Interfaces;

namespace NexusRDM.Core.Services;

/// <summary>
/// Wraps Windows Credential Manager via advapi32 P/Invoke. All NexusRDM
/// credentials are stored under the "NexusRDM/" prefix so they are easy to
/// identify and enumerate. Credentials are NEVER written to the SQLite database.
/// </summary>
public sealed class CredentialVault : ICredentialVault
{
    private const string Prefix = "NexusRDM/";

    public string Save(string key, string username, string password)
    {
        var target  = Prefix + key;
        var blob    = Encoding.Unicode.GetBytes(password ?? string.Empty);
        var blobPtr = IntPtr.Zero;

        try
        {
            blobPtr = Marshal.AllocCoTaskMem(blob.Length);
            Marshal.Copy(blob, 0, blobPtr, blob.Length);

            var cred = new CREDENTIAL
            {
                Type               = CRED_TYPE_GENERIC,
                TargetName         = target,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob     = blobPtr,
                Persist            = CRED_PERSIST_LOCAL_MACHINE,
                UserName           = username,
            };

            if (!CredWriteW(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CredWrite failed for '{target}'.");
        }
        finally
        {
            if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
        }

        return key;
    }

    public (string Username, string Password)? Load(string key)
    {
        var target = Prefix + key;
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err, $"CredRead failed for '{target}'.");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            string user = cred.UserName ?? string.Empty;
            string pass = cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2))
                : string.Empty;
            return (user, pass);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public void Delete(string key)
    {
        var target = Prefix + key;
        if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return;
            throw new Win32Exception(err, $"CredDelete failed for '{target}'.");
        }
    }

    public IReadOnlyList<string> ListKeys()
    {
        if (!CredEnumerateW(Prefix + "*", 0, out uint count, out IntPtr arrayPtr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return Array.Empty<string>();
            throw new Win32Exception(err, "CredEnumerate failed.");
        }

        try
        {
            var keys = new List<string>((int)count);
            for (int i = 0; i < count; i++)
            {
                var entryPtr = Marshal.ReadIntPtr(arrayPtr, i * IntPtr.Size);
                var cred     = Marshal.PtrToStructure<CREDENTIAL>(entryPtr);
                if (cred.TargetName is { } t && t.StartsWith(Prefix, StringComparison.Ordinal))
                    keys.Add(t[Prefix.Length..]);
            }
            return keys;
        }
        finally
        {
            CredFree(arrayPtr);
        }
    }

    // ── advapi32 interop ──────────────────────────────────────────────────────

    private const uint CRED_TYPE_GENERIC          = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int  ERROR_NOT_FOUND            = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint   Flags;
        public uint   Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint   CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint   Persist;
        public uint   AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr credentialsPtr);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);
}
