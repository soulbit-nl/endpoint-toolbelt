// Reading and deleting files by handle without following links. The handle-relative folder work
// (opening a folder by handle, writing and deleting relative to it) is in NativeFileFolders.cs.
//
// OpenReadNoFollow refuses a link, a folder or a file with more than one hard link; the checks
// and the read use the same handle. DeleteFileBelowNoFollow deletes one file below a folder a
// user can write to (a profile) by opening each path component relative to its pinned parent
// handle with OBJ_DONT_REPARSE (see docs/security.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Written for the C# 5 subset (no Marshal.SizeOf<T>); the status text is ASCII hex.
#pragma warning disable CA2263, CA1838, CA1305

namespace EndpointToolbelt.Platform
{
    public static partial class NativeFile
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        private const uint GenericRead = 0x80000000;
        private const uint ShareAll = 0x7;
        private const uint OpenExisting = 3;
        private const uint FlagOpenReparsePoint = 0x00200000;
        private const uint FlagBackupSemantics = 0x02000000;
        private const uint FlagSequentialScan = 0x08000000;
        private const uint AttributeDirectory = 0x10;
        private const uint AttributeReparsePoint = 0x400;

        public static string ToExtendedPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) { return path; }
            if (path.Length >= 3 && path[1] == ':' && path[2] == '\\') { return @"\\?\" + path; }
            return path;
        }

        public static FileStream OpenReadNoFollow(string path)
        {
            SafeFileHandle handle = CreateFileW(ToExtendedPath(path), GenericRead, ShareAll, IntPtr.Zero, OpenExisting,
                FlagOpenReparsePoint | FlagBackupSemantics | FlagSequentialScan, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("The file could not be opened (error " + error + ").", unchecked((int)0x80070000) | error);
            }
            try
            {
                FileInformation information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("The file could not be inspected (error " + error + ").");
                }
                if ((information.FileAttributes & AttributeReparsePoint) != 0) { throw new IOException("The file is a link or another reparse point."); }
                if ((information.FileAttributes & AttributeDirectory) != 0) { throw new IOException("The path is a folder."); }
                if (information.NumberOfLinks != 1) { throw new IOException("The file has more than one hard link."); }
                return new FileStream(handle, FileAccess.Read, 81920);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        // ---- Link-safe delete of one file below a user-writable root --------------------------
        //
        // Each component below the root is opened RELATIVE to its already-opened parent handle
        // (NtOpenFile with OBJECT_ATTRIBUTES.RootDirectory = parent), with OBJ_DONT_REPARSE and
        // FILE_OPEN_REPARSE_POINT, so no component can be swapped for a junction or an
        // object-manager symlink between the opens. Every opened handle forbids FILE_SHARE_DELETE,
        // so a pinned level cannot be renamed or replaced while it is held. The leaf must be a
        // regular file with one hard link, on the same volume as the root, and must resolve back
        // to exactly root\relative; it is then deleted through its own handle.

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            [MarshalAs(UnmanagedType.U1)]
            public bool DeleteFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfoEx
        {
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RedirectionTrustPolicy
        {
            public uint Flags;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtOpenFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status, uint share, uint options);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
        private static extern bool SetFileDisposition(SafeFileHandle handle, int infoClass, ref FileDispositionInfo info, uint size);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
        private static extern bool SetFileDispositionEx(SafeFileHandle handle, int infoClass, ref FileDispositionInfoEx info, uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] System.Text.StringBuilder path, int count, int flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessMitigationPolicy(int policy, ref RedirectionTrustPolicy buffer, IntPtr length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, ref RedirectionTrustPolicy buffer, IntPtr length);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private const uint FileListDirectory = 0x1;
        private const uint FileReadAttributes = 0x80;
        private const uint Delete = 0x10000;
        private const uint Synchronize = 0x100000;
        private const uint ShareReadWrite = 0x3;

        private const uint FileDirectoryFile = 0x1;
        private const uint FileNonDirectoryFile = 0x40;
        private const uint FileSynchronousIoNonAlert = 0x20;
        private const uint FileOpenReparsePoint = 0x00200000;

        private const uint ObjCaseInsensitive = 0x40;
        private const uint ObjDontReparse = 0x1000;

        private const int FileDispositionInfoClass = 4;
        private const int FileDispositionInfoExClass = 21;
        private const uint DispositionDelete = 0x1;
        private const uint DispositionPosix = 0x2;
        private const uint DispositionIgnoreReadonly = 0x10;

        private const int ErrorFileNotFound = 2;
        private const int ErrorPathNotFound = 3;
        // PROCESS_MITIGATION_POLICY: 14 is ProcessSideChannelIsolationPolicy, 16 is
        // ProcessRedirectionTrustPolicy (winnt.h order).
        private const int ProcessRedirectionTrustPolicy = 16;

        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusObjectPathNotFound = unchecked((int)0xC000003A);
        private const int StatusObjectNameInvalid = unchecked((int)0xC0000033);
        private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);
        private const int StatusInvalidParameter = unchecked((int)0xC000000D);
        private const int StatusNotADirectory = unchecked((int)0xC0000103);
        private const int StatusFileIsADirectory = unchecked((int)0xC00000BA);

        // Best effort, once: refuse to follow redirections planted by a less-trusted process.
        // Missing on Windows 10 before 1809; any failure is ignored by design.
        public static bool TryEnableRedirectionGuard()
        {
            try
            {
                RedirectionTrustPolicy policy = new RedirectionTrustPolicy();
                policy.Flags = 1; // EnforceRedirectionTrust
                return SetProcessMitigationPolicy(ProcessRedirectionTrustPolicy, ref policy, (IntPtr)Marshal.SizeOf(typeof(RedirectionTrustPolicy)));
            }
            catch
            {
                return false;
            }
        }

        // True when this process enforces redirection trust, read back from the system.
        public static bool IsRedirectionGuardEnabled()
        {
            try
            {
                RedirectionTrustPolicy policy = new RedirectionTrustPolicy();
                if (!GetProcessMitigationPolicy(GetCurrentProcess(), ProcessRedirectionTrustPolicy, ref policy, (IntPtr)Marshal.SizeOf(typeof(RedirectionTrustPolicy)))) { return false; }
                return (policy.Flags & 1) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static int OpenRelative(SafeFileHandle parent, string name, uint access, uint options, out SafeFileHandle result)
        {
            result = null;
            IntPtr namePtr = Marshal.StringToHGlobalUni(name);
            IntPtr usPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
            bool parentRef = false;
            try
            {
                UnicodeString us = new UnicodeString();
                us.Length = (ushort)(name.Length * 2);
                us.MaximumLength = (ushort)(name.Length * 2);
                us.Buffer = namePtr;
                Marshal.StructureToPtr(us, usPtr, false);

                ObjectAttributes oa = new ObjectAttributes();
                oa.Length = Marshal.SizeOf(typeof(ObjectAttributes));
                parent.DangerousAddRef(ref parentRef);
                oa.RootDirectory = parent.DangerousGetHandle();
                oa.ObjectName = usPtr;
                oa.Attributes = ObjCaseInsensitive | ObjDontReparse;
                oa.SecurityDescriptor = IntPtr.Zero;
                oa.SecurityQualityOfService = IntPtr.Zero;

                IoStatusBlock iosb;
                IntPtr handle;
                int status = NtOpenFile(out handle, access, ref oa, out iosb, ShareReadWrite, options);
                if (status == 0) { result = new SafeFileHandle(handle, true); }
                return status;
            }
            finally
            {
                if (parentRef) { parent.DangerousRelease(); }
                Marshal.FreeHGlobal(usPtr);
                Marshal.FreeHGlobal(namePtr);
            }
        }

        private static bool IsNotFound(int status)
        {
            return status == StatusObjectNameNotFound || status == StatusObjectPathNotFound;
        }

        private static void ThrowForStatus(int status, string what)
        {
            if (status == StatusReparsePointEncountered) { throw new IOException(what + " is a link or another reparse point."); }
            if (status == StatusNotADirectory) { throw new IOException("A part of the path is not a folder."); }
            if (status == StatusFileIsADirectory) { throw new IOException("The path is a folder."); }
            if (status == StatusInvalidParameter || status == StatusObjectNameInvalid)
            {
                // OBJ_DONT_REPARSE is not supported on this build: refuse rather than fall back to
                // a weaker open, so a planted link can never be followed (fail closed).
                throw new IOException("This Windows build does not support a link-safe open; " + what + " was not touched.");
            }
            throw new IOException(what + " could not be opened (status 0x" + status.ToString("X8") + ").");
        }

        private static string GetFinalPath(SafeFileHandle handle)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
            int len = GetFinalPathNameByHandleW(handle, sb, sb.Capacity, 0);
            if (len == 0) { throw new IOException("The file path could not be resolved (error " + Marshal.GetLastWin32Error() + ")."); }
            if (len > sb.Capacity)
            {
                sb = new System.Text.StringBuilder(len + 1);
                len = GetFinalPathNameByHandleW(handle, sb, sb.Capacity, 0);
                if (len == 0) { throw new IOException("The file path could not be resolved (error " + Marshal.GetLastWin32Error() + ")."); }
            }
            return sb.ToString();
        }

        private static string StripExtendedPrefix(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) { return path.Substring(4); }
            return path;
        }

        private static void DeleteViaHandle(SafeFileHandle file)
        {
            FileDispositionInfoEx ex = new FileDispositionInfoEx();
            ex.Flags = DispositionDelete | DispositionPosix | DispositionIgnoreReadonly;
            if (SetFileDispositionEx(file, FileDispositionInfoExClass, ref ex, (uint)Marshal.SizeOf(typeof(FileDispositionInfoEx)))) { return; }
            int error = Marshal.GetLastWin32Error();
            // Older OS without FileDispositionInfoEx: fall back to the basic delete flag.
            if (error == 87 || error == 50 || error == 1)
            {
                FileDispositionInfo basic = new FileDispositionInfo();
                basic.DeleteFile = true;
                if (SetFileDisposition(file, FileDispositionInfoClass, ref basic, (uint)Marshal.SizeOf(typeof(FileDispositionInfo)))) { return; }
                error = Marshal.GetLastWin32Error();
            }
            throw new IOException("The file could not be deleted (error " + error + ").", unchecked((int)0x80070000) | error);
        }

        public static bool DeleteFileBelowNoFollow(string root, string relativePath)
        {
            string[] parts = relativePath.Split('\\');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..") { throw new ArgumentException("The relative path is not plain."); }
            }
            List<SafeFileHandle> pinned = new List<SafeFileHandle>();
            try
            {
                string rootTrim = root.TrimEnd('\\');
                // Only the root is opened by path. It may itself be an FSLogix profile-container
                // mount point (a reparse point), which is expected, so the root is not refused for
                // being one; every component below it is opened relative to a pinned handle.
                SafeFileHandle rootHandle = CreateFileW(ToExtendedPath(rootTrim), FileListDirectory | FileReadAttributes, ShareReadWrite, IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero);
                if (rootHandle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    rootHandle.Dispose();
                    if (error == ErrorFileNotFound || error == ErrorPathNotFound) { return false; }
                    throw new IOException("The profile root could not be opened (error " + error + ").", unchecked((int)0x80070000) | error);
                }
                pinned.Add(rootHandle);
                FileInformation rootInfo;
                if (!GetFileInformationByHandle(rootHandle, out rootInfo)) { throw new IOException("The profile root could not be inspected."); }
                uint rootVolume = rootInfo.VolumeSerialNumber;

                SafeFileHandle parent = rootHandle;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    SafeFileHandle child;
                    int status = OpenRelative(parent, parts[i], FileListDirectory | FileReadAttributes | Synchronize,
                        FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert, out child);
                    if (IsNotFound(status)) { return false; }
                    if (status != 0) { ThrowForStatus(status, "a folder on the path"); }
                    FileInformation info;
                    if (!GetFileInformationByHandle(child, out info)) { child.Dispose(); throw new IOException("A folder on the path could not be inspected."); }
                    if ((info.FileAttributes & AttributeReparsePoint) != 0) { child.Dispose(); throw new IOException("A folder on the path is a link or another reparse point."); }
                    if ((info.FileAttributes & AttributeDirectory) == 0) { child.Dispose(); throw new IOException("A part of the path is not a folder."); }
                    pinned.Add(child);
                    parent = child;
                }

                SafeFileHandle file;
                int fileStatus = OpenRelative(parent, parts[parts.Length - 1], Delete | FileReadAttributes | Synchronize,
                    FileNonDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert, out file);
                if (IsNotFound(fileStatus)) { return false; }
                if (fileStatus != 0) { ThrowForStatus(fileStatus, "the file"); }
                using (file)
                {
                    FileInformation info;
                    if (!GetFileInformationByHandle(file, out info)) { throw new IOException("The file could not be inspected."); }
                    if ((info.FileAttributes & AttributeReparsePoint) != 0) { throw new IOException("The file is a link or another reparse point."); }
                    if ((info.FileAttributes & AttributeDirectory) != 0) { throw new IOException("The path is a folder."); }
                    if (info.NumberOfLinks != 1) { throw new IOException("The file has more than one hard link."); }
                    if (info.VolumeSerialNumber != rootVolume) { throw new IOException("The file is on another volume than the profile root."); }
                    string finalPath = StripExtendedPrefix(GetFinalPath(file));
                    string expected = rootTrim + "\\" + relativePath;
                    if (!string.Equals(finalPath, expected, StringComparison.OrdinalIgnoreCase)) { throw new IOException("The file resolved to another path than expected."); }
                    DeleteViaHandle(file);
                }
                return true;
            }
            finally
            {
                for (int i = pinned.Count - 1; i >= 0; i--) { pinned[i].Dispose(); }
            }
        }
    }
}
