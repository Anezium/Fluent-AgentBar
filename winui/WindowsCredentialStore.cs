using System.Runtime.InteropServices;
using System.Text;

namespace FluentAgentBar;

// Minimal read-only access to the Windows Credential Manager (generic
// credentials). The Antigravity CLI keeps its Google OAuth material there
// under the target "gemini:antigravity".
internal static class WindowsCredentialStore
{
    private const uint CredTypeGeneric = 1;

    internal static string? ReadGenericBlob(string targetName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr credentialPtr = IntPtr.Zero;
        try
        {
            if (!CredReadW(targetName, CredTypeGeneric, 0, out credentialPtr) || credentialPtr == IntPtr.Zero)
            {
                return null;
            }

            Credential credential = Marshal.PtrToStructure<Credential>(credentialPtr);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            byte[] blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);

            // Generic blobs are opaque bytes; the tools we read store UTF-8 JSON,
            // but PowerShell-written entries are UTF-16LE, so sniff for NULs.
            string text = Encoding.UTF8.GetString(blob);
            return text.Contains('\0') ? Encoding.Unicode.GetString(blob) : text;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (credentialPtr != IntPtr.Zero)
            {
                CredFree(credentialPtr);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);
}
