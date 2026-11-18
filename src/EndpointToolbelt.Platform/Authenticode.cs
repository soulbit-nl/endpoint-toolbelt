using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EndpointToolbelt.Platform;

/// <summary>
/// WinVerifyTrust for a file (scripts are handled by the PowerShell subject interface package).
/// </summary>
public static class Authenticode
{
    /// <summary>
    /// True when the file carries a valid embedded signature whose signer is Microsoft
    /// ("O=Microsoft Corporation" in the subject), as the tools SYSTEM runs from outside System32
    /// (Defender's MpCmdRun.exe) do. A file signed only through a catalog is refused: fail closed.
    /// </summary>
    public static bool IsMicrosoftSigned(string path)
    {
        try
        {
            if (!IsTrusted(path)) { return false; }
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return signer.Subject.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch (Exception)
        {
            // Unsigned, a catalog signature only, or unreadable: never trusted.
            return false;
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public int cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public int dwUIChoice;
        public int fdwRevocationChecks;
        public int dwUnionChoice;
        public IntPtr pFile;
        public int dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public int dwProvFlags;
        public int dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    public static bool IsTrusted(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(fileInfo.cbStruct);
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
                dwProvFlags = 0x1000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
