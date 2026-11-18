// Handle-relative folder work: a folder is opened once by handle, checked (a real
// directory, not a reparse point, at the expected final path, with its security descriptor read
// through the handle), and everything below it is opened RELATIVE to that handle with
// OBJ_DONT_REPARSE. Nothing below the pinned handle can be swapped for a link between the check
// and the write or delete; see docs/security.md.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

#pragma warning disable CA2263, CA1838, CA1305

namespace EndpointToolbelt.Platform
{
    public static partial class NativeFile
    {
        [DllImport("ntdll.dll")]
        private static extern int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
            IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision, uint securityInfo, out IntPtr text, out uint length);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetFileInformationByHandle")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationRaw(SafeFileHandle handle, int infoClass, IntPtr info, uint size);

        // The Win32 rename refuses a RootDirectory handle (error 87); the native call takes it.
        [DllImport("ntdll.dll")]
        private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status, IntPtr info, uint length, int infoClass);

        private const int FileRenameInformationClass = 10;

        private const int SeFileObject = 1;
        private const uint OwnerSecurityInformation = 0x1;
        private const uint DaclSecurityInformation = 0x4;
        private const uint SddlRevision1 = 1;

        private const uint FileAppendData = 0x4;
        private const uint FileWriteAttributes = 0x100;
        private const uint ReadControl = 0x20000;
        private const uint WriteDac = 0x40000;
        private const uint ShareAllThree = 0x7;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileAttributeReadOnly = 0x1;
        private const uint FileOpenIf = 3;
        private const int FileBasicInfoClass = 0;
        private const int ErrorAccessDenied = 5;

        /// <summary>
        /// Opens a folder by handle without following a link: it must be a real directory (not a
        /// reparse point) whose final path is the path asked for. Null when the folder does not
        /// exist. The handle forbids renames of the folder while it is held.
        /// </summary>
        public static SafeFileHandle OpenFolderNoFollow(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            SafeFileHandle handle = CreateFileW(ToExtendedPath(full), FileListDirectory | FileReadAttributes | ReadControl, ShareReadWrite, IntPtr.Zero,
                OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == ErrorFileNotFound || error == ErrorPathNotFound) { return null; }
                throw new IOException("The folder could not be opened (error " + error + ").", unchecked((int)0x80070000) | error);
            }
            try
            {
                FileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) { throw new IOException("The folder could not be inspected."); }
                if ((info.FileAttributes & AttributeReparsePoint) != 0) { throw new IOException("The folder is a link or another reparse point."); }
                if ((info.FileAttributes & AttributeDirectory) == 0) { throw new IOException("The path is not a folder."); }
                string finalPath = StripExtendedPrefix(GetFinalPath(handle));
                if (!string.Equals(finalPath, full, StringComparison.OrdinalIgnoreCase)) { throw new IOException("The folder resolved to another path than expected."); }
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        /// <summary>The owner and DACL of an open file or folder, read through its handle.</summary>
        public static FileSystemSecurity ReadSecurity(SafeFileHandle handle)
        {
            IntPtr owner, group, dacl, sacl, descriptor;
            uint result = GetSecurityInfo(handle, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation, out owner, out group, out dacl, out sacl, out descriptor);
            if (result != 0) { throw new IOException("The security descriptor could not be read (error " + result + ")."); }
            try
            {
                IntPtr text;
                uint length;
                if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, SddlRevision1, OwnerSecurityInformation | DaclSecurityInformation, out text, out length))
                {
                    throw new IOException("The security descriptor could not be converted (error " + Marshal.GetLastWin32Error() + ").");
                }
                try
                {
                    string sddl = Marshal.PtrToStringUni(text);
                    DirectorySecurity security = new DirectorySecurity();
                    security.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Owner | AccessControlSections.Access);
                    return security;
                }
                finally { LocalFree(text); }
            }
            finally { LocalFree(descriptor); }
        }

        private static int CreateRelative(SafeFileHandle parent, string name, uint access, uint share, uint disposition, uint options, out SafeFileHandle result)
        {
            result = null;
            if (name.Length == 0 || name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name == "." || name == "..") { throw new ArgumentException("The name is not a plain file name."); }
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
                IoStatusBlock iosb;
                IntPtr handle;
                int status = NtCreateFile(out handle, access, ref oa, out iosb, IntPtr.Zero, FileAttributeNormal, share, disposition, options, IntPtr.Zero, 0);
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

        private static FileInformation Inspect(SafeFileHandle handle, string what)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) { throw new IOException(what + " could not be inspected (error " + Marshal.GetLastWin32Error() + ")."); }
            return info;
        }

        /// <summary>
        /// Opens (or creates) a file directly in <paramref name="folder"/> for appending, relative
        /// to the folder handle; refuses a link and a file with more than one hard link. The handle
        /// also allows reading and changing the file's DACL, so the caller can protect it.
        /// </summary>
        public static FileStream OpenAppendRelative(SafeFileHandle folder, string name)
        {
            SafeFileHandle file;
            int status = CreateRelative(folder, name, FileAppendData | FileReadAttributes | ReadControl | WriteDac | Synchronize, ShareAllThree, FileOpenIf,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, out file);
            if (status != 0) { ThrowForStatus(status, "the log file"); }
            try
            {
                FileInformation info = Inspect(file, "The log file");
                if ((info.FileAttributes & AttributeReparsePoint) != 0) { throw new IOException("The log file is a link or another reparse point."); }
                if ((info.FileAttributes & AttributeDirectory) != 0) { throw new IOException("The log file path is a folder."); }
                if (info.NumberOfLinks != 1) { throw new IOException("The log file has more than one hard link."); }
                return new FileStream(file, FileAccess.Write, 4096);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The size of a file directly in <paramref name="folder"/>, or null when it does not exist
        /// or is a link (a link is never rolled over or deleted through).
        /// </summary>
        public static long? GetFileLengthRelative(SafeFileHandle folder, string name)
        {
            SafeFileHandle file;
            int status = OpenRelative(folder, name, FileReadAttributes | Synchronize, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, out file);
            if (IsNotFound(status)) { return null; }
            if (status != 0) { ThrowForStatus(status, "the file"); }
            using (file)
            {
                FileInformation info = Inspect(file, "The file");
                if ((info.FileAttributes & AttributeReparsePoint) != 0) { return null; }
                return ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            }
        }

        /// <summary>Opens a regular file directly in <paramref name="folder"/> with the given access; refuses links and hard-linked files.</summary>
        public static SafeFileHandle OpenFileRelative(SafeFileHandle folder, string name, uint access)
        {
            SafeFileHandle file;
            int status = OpenRelative(folder, name, access | FileReadAttributes | Synchronize, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, out file);
            if (IsNotFound(status)) { return null; }
            if (status != 0) { ThrowForStatus(status, "the file"); }
            try
            {
                FileInformation info = Inspect(file, "The file");
                if ((info.FileAttributes & AttributeReparsePoint) != 0) { throw new IOException("The file is a link or another reparse point."); }
                if (info.NumberOfLinks != 1) { throw new IOException("The file has more than one hard link."); }
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        /// <summary>Access rights for <see cref="OpenFileRelative"/> that allow reading and changing the DACL.</summary>
        public const uint AccessReadControlWriteDac = ReadControl | WriteDac;

        /// <summary>Deletes a file directly in <paramref name="folder"/> through its handle (a link is removed as a link). False when it does not exist.</summary>
        public static bool DeleteFileRelative(SafeFileHandle folder, string name)
        {
            SafeFileHandle file;
            int status = OpenRelative(folder, name, Delete | FileReadAttributes | FileWriteAttributes | Synchronize, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, out file);
            if (IsNotFound(status)) { return false; }
            if (status != 0) { ThrowForStatus(status, "the file"); }
            using (file)
            {
                DeleteEntryViaHandle(file, Inspect(file, "The file"));
            }
            return true;
        }

        /// <summary>
        /// Renames a regular file directly in <paramref name="folder"/> to another name in the same
        /// folder, through its handle; never replaces an existing file.
        /// </summary>
        public static void RenameFileRelative(SafeFileHandle folder, string from, string to)
        {
            if (to.Length == 0 || to.IndexOf('\\') >= 0 || to.IndexOf('/') >= 0 || to == "." || to == "..") { throw new ArgumentException("The new name is not a plain file name."); }
            SafeFileHandle file;
            int status = OpenRelative(folder, from, Delete | FileReadAttributes | Synchronize, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, out file);
            if (status != 0) { ThrowForStatus(status, "the file"); }
            using (file)
            {
                FileInformation info = Inspect(file, "The file");
                if ((info.FileAttributes & AttributeReparsePoint) != 0) { throw new IOException("The file is a link or another reparse point."); }
                // FILE_RENAME_INFO: ReplaceIfExists (padded to pointer size), RootDirectory, FileNameLength, FileName.
                int nameOffset = 2 * IntPtr.Size + 4;
                byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(to);
                int size = nameOffset + nameBytes.Length + 2;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                bool folderRef = false;
                try
                {
                    for (int i = 0; i < size; i++) { Marshal.WriteByte(buffer, i, 0); }
                    folder.DangerousAddRef(ref folderRef);
                    Marshal.WriteIntPtr(buffer, IntPtr.Size, folder.DangerousGetHandle());
                    Marshal.WriteInt32(buffer, 2 * IntPtr.Size, nameBytes.Length);
                    Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);
                    IoStatusBlock iosb;
                    int renameStatus = NtSetInformationFile(file, out iosb, buffer, (uint)size, FileRenameInformationClass);
                    if (renameStatus != 0)
                    {
                        if (renameStatus == unchecked((int)0xC0000035)) { throw new IOException("A file with the new name already exists."); }
                        throw new IOException("The file could not be renamed (status 0x" + renameStatus.ToString("X8") + ").");
                    }
                }
                finally
                {
                    if (folderRef) { folder.DangerousRelease(); }
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        /// <summary>
        /// Deletes the tree at root\relativePath without following links: the root is opened by
        /// path and must be a real directory (a root that is a reparse point is refused, nothing is
        /// touched); every entry below it is opened relative to its pinned parent handle with
        /// OBJ_DONT_REPARSE, must be on the root's volume, and is deleted through its own handle. A
        /// link inside the tree is removed as a link; its target is never entered. False when the
        /// root or the path does not exist.
        /// </summary>
        public static bool DeleteTreeBelowNoFollow(string root, string relativePath)
        {
            string[] parts = relativePath.Split('\\');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..") { throw new ArgumentException("The relative path is not plain."); }
            }
            List<SafeFileHandle> pinned = new List<SafeFileHandle>();
            try
            {
                string rootTrim = Path.GetFullPath(root).TrimEnd('\\');
                SafeFileHandle rootHandle = CreateFileW(ToExtendedPath(rootTrim), FileListDirectory | FileReadAttributes, ShareReadWrite, IntPtr.Zero, OpenExisting,
                    FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);
                if (rootHandle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    rootHandle.Dispose();
                    if (error == ErrorFileNotFound || error == ErrorPathNotFound) { return false; }
                    throw new IOException("The root folder could not be opened (error " + error + ").", unchecked((int)0x80070000) | error);
                }
                pinned.Add(rootHandle);
                FileInformation rootInfo = Inspect(rootHandle, "The root folder");
                if ((rootInfo.FileAttributes & AttributeReparsePoint) != 0) { throw new IOException("The root folder is a link or another reparse point; nothing was removed."); }
                if ((rootInfo.FileAttributes & AttributeDirectory) == 0) { throw new IOException("The root path is not a folder."); }
                uint rootVolume = rootInfo.VolumeSerialNumber;

                SafeFileHandle parent = rootHandle;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    SafeFileHandle child;
                    int status = OpenRelative(parent, parts[i], FileListDirectory | FileReadAttributes | Synchronize, FileDirectoryFile | FileOpenReparsePoint | FileSynchronousIoNonAlert, out child);
                    if (IsNotFound(status)) { return false; }
                    if (status != 0) { ThrowForStatus(status, "a folder on the path"); }
                    FileInformation info = Inspect(child, "A folder on the path");
                    if ((info.FileAttributes & AttributeReparsePoint) != 0) { child.Dispose(); throw new IOException("A folder on the path is a link or another reparse point."); }
                    if (info.VolumeSerialNumber != rootVolume) { child.Dispose(); throw new IOException("A folder on the path is on another volume than the root."); }
                    pinned.Add(child);
                    parent = child;
                }

                SafeFileHandle target;
                int targetStatus = OpenRelative(parent, parts[parts.Length - 1], Delete | FileListDirectory | FileReadAttributes | FileWriteAttributes | Synchronize,
                    FileOpenReparsePoint | FileSynchronousIoNonAlert, out target);
                if (IsNotFound(targetStatus)) { return false; }
                if (targetStatus != 0) { ThrowForStatus(targetStatus, "the entry"); }
                using (target) { DeleteEntryRecursive(target, rootVolume); }
                return true;
            }
            finally
            {
                for (int i = pinned.Count - 1; i >= 0; i--) { pinned[i].Dispose(); }
            }
        }

        private static void DeleteEntryRecursive(SafeFileHandle entry, uint rootVolume)
        {
            FileInformation info = Inspect(entry, "The entry");
            if (info.VolumeSerialNumber != rootVolume) { throw new IOException("The entry is on another volume than the root."); }
            bool isLink = (info.FileAttributes & AttributeReparsePoint) != 0;
            if (!isLink && (info.FileAttributes & AttributeDirectory) != 0)
            {
                // Names come from a listing by path; each one is then opened relative to this
                // pinned handle, so a name swapped for a link after the listing is still refused.
                List<string> names = new List<string>();
                foreach (string child in Directory.EnumerateFileSystemEntries(GetFinalPath(entry))) { names.Add(Path.GetFileName(child)); }
                IOException first = null;
                foreach (string name in names)
                {
                    SafeFileHandle child;
                    int status = OpenRelative(entry, name, Delete | FileListDirectory | FileReadAttributes | FileWriteAttributes | Synchronize, FileOpenReparsePoint | FileSynchronousIoNonAlert, out child);
                    if (IsNotFound(status)) { continue; }
                    try
                    {
                        if (status != 0) { ThrowForStatus(status, "an entry below the folder"); }
                        using (child) { DeleteEntryRecursive(child, rootVolume); }
                    }
                    catch (IOException ex)
                    {
                        if (first == null) { first = ex; }
                    }
                }
                if (first != null) { throw first; }
            }
            DeleteEntryViaHandle(entry, info);
        }

        private static void DeleteEntryViaHandle(SafeFileHandle entry, FileInformation info)
        {
            bool isLink = (info.FileAttributes & AttributeReparsePoint) != 0;
            if (!isLink && (info.FileAttributes & FileAttributeReadOnly) != 0) { ClearReadOnly(entry); }
            DeleteViaHandle(entry);
        }

        // FILE_BASIC_INFO: four LARGE_INTEGER times (0 = leave unchanged) and the attributes.
        private static void ClearReadOnly(SafeFileHandle entry)
        {
            IntPtr buffer = Marshal.AllocHGlobal(40);
            try
            {
                for (int i = 0; i < 40; i++) { Marshal.WriteByte(buffer, i, 0); }
                Marshal.WriteInt32(buffer, 32, (int)FileAttributeNormal);
                if (!SetFileInformationRaw(entry, FileBasicInfoClass, buffer, 40))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("The read-only attribute could not be cleared (error " + error + ").", unchecked((int)0x80070000) | error);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}