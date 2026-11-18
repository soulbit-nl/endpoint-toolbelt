using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace EndpointToolbelt.Platform;

/// <summary>Well-known SIDs used by the folder rules.</summary>
public static class WellKnownSids
{
    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier CreatorOwner = new(WellKnownSidType.CreatorOwnerSid, null);
    public static readonly SecurityIdentifier TrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
}

/// <summary>Owners and full-control principals a protected folder must have, and nothing else.</summary>
public sealed class FolderPolicy
{
    public FolderPolicy(IEnumerable<SecurityIdentifier> allowedOwners, IEnumerable<SecurityIdentifier> fullControl)
    {
        AllowedOwners = allowedOwners.ToList();
        FullControl = fullControl.ToList();
    }

    public IReadOnlyList<SecurityIdentifier> AllowedOwners { get; }
    public IReadOnlyList<SecurityIdentifier> FullControl { get; }

    /// <summary>SYSTEM and Administrators only; the policy of the service's working folder.</summary>
    public static FolderPolicy SystemOnly { get; } = new(
        new[] { WellKnownSids.LocalSystem, WellKnownSids.Administrators },
        new[] { WellKnownSids.LocalSystem, WellKnownSids.Administrators });

    public DirectorySecurity CreateSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in FullControl)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }
}

public static class FileSystemSafety
{
    /// <summary>True when the path itself or any existing parent is a reparse point.</summary>
    public static bool HasReparsePointInPath(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current != null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) { return true; }
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Returns null when the folder matches the policy exactly, otherwise the reason it does not.
    /// The DACL must be protected and contain exactly one inheritable full-control allow ACE per
    /// policy principal (compared as a set; order does not matter).
    /// </summary>
    public static string ValidateFolder(string path, FolderPolicy policy)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) { return "missing"; }
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { return "reparse point"; }
        if (HasReparsePointInPath(path)) { return "reparse point in path"; }

        var security = info.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !policy.AllowedOwners.Contains(owner)) { return "unexpected owner"; }
        if (!security.AreAccessRulesProtected) { return "inherits permissions"; }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        if (rules.Count != policy.FullControl.Count) { return "unexpected access entries"; }
        foreach (var sid in policy.FullControl)
        {
            var match = rules.Count(r =>
                r.AccessControlType == AccessControlType.Allow &&
                ((SecurityIdentifier)r.IdentityReference).Equals(sid) &&
                (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
                r.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                r.PropagationFlags == PropagationFlags.None);
            if (match != 1) { return "unexpected access entries"; }
        }
        return null;
    }

    /// <summary>
    /// Makes sure <paramref name="path"/> is a protected folder. An existing folder that does not
    /// match the policy is moved aside. A new folder is created "born locked": with its final
    /// security descriptor, under a random name, verified, then renamed into place. The rename
    /// fails if something appeared at the final name in the meantime.
    /// </summary>
    public static void EnsureProtectedFolder(string path, FolderPolicy policy)
    {
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path) ?? throw new ArgumentException("Path has no parent.", nameof(path));
        if (HasReparsePointInPath(parent)) { throw new IOException($"Refusing to use {path}: a parent folder is a reparse point."); }

        if (Directory.Exists(path) || File.Exists(path))
        {
            if (Directory.Exists(path) && ValidateFolder(path, policy) == null) { return; }
            // Move the untrusted entry aside (a reparse point is moved as a link, not followed).
            var aside = path + ".rejected-" + Guid.NewGuid().ToString("N");
            if (Directory.Exists(path)) { Directory.Move(path, aside); } else { File.Move(path, aside); }
        }

        var staging = path + ".new-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging, policy.CreateSecurity());
        var reason = ValidateFolder(staging, policy);
        if (reason != null)
        {
            DeleteTreeNoFollow(staging);
            throw new IOException($"The new folder {staging} does not have the expected security ({reason}).");
        }
        Directory.Move(staging, path);
    }

    /// <summary>
    /// Deletes a folder tree without following reparse points: a junction or symbolic link is
    /// removed as a link; its target is never entered.
    /// </summary>
    public static void DeleteTreeNoFollow(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            if (File.Exists(path)) { DeleteFileNoFollow(path); }
            return;
        }
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Removing a directory link deletes the link, not the target.
            info.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(path, recursive: false);
            return;
        }
        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo) { DeleteTreeNoFollow(entry.FullName); }
            else { DeleteFileNoFollow(entry.FullName); }
        }
        info.Attributes &= ~FileAttributes.ReadOnly;
        Directory.Delete(path, recursive: false);
    }

    private static void DeleteFileNoFollow(string path)
    {
        // DeleteFile on a file symbolic link deletes the link itself. Attributes are changed only
        // on plain files (SetFileAttributes would follow a link to its target).
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        File.Delete(path);
    }

    /// <summary>
    /// Returns null when a folder inside a protected root has no explicit access entries and
    /// inherits from its parent (so it carries the root's SYSTEM-only DACL).
    /// </summary>
    public static string ValidateInheritingFolder(string path)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists) { return "missing"; }
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) { return "reparse point"; }
        var security = info.GetAccessControl(AccessControlSections.Access);
        if (security.AreAccessRulesProtected) { return "does not inherit permissions"; }
        if (security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Count > 0)
        {
            return "has explicit access entries";
        }
        return null;
    }

    /// <summary>Creates a subfolder of a protected root, replacing one that does not simply inherit.</summary>
    public static void EnsureInheritingFolder(string path)
    {
        if (Directory.Exists(path) && ValidateInheritingFolder(path) != null) { DeleteTreeNoFollow(path); }
        Directory.CreateDirectory(path);
    }

    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>True for Windows device names (with or without an extension), e.g. "NUL.zip".</summary>
    public static bool IsReservedDeviceName(string fileName)
    {
        var stem = fileName.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    // --- Opening files without following links --------------------------------------------------

    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_APPEND_DATA = 0x0004;
    private const uint GENERIC_READ = 0x80000000;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint OPEN_ALWAYS = 4;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION info);

    private static SafeFileHandle OpenNoFollow(string path, uint access, uint disposition)
    {
        var handle = CreateFile(path, access | FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, disposition, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {path}."); }
        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        if ((info.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || info.NumberOfLinks != 1)
        {
            handle.Dispose();
            throw new LinkRefusedException($"Refusing {path}: it is a link or has more than one hard link.");
        }
        return handle;
    }

    /// <summary>Opens (or creates) a file for appending; refuses links and hard-linked files.</summary>
    public static FileStream OpenAppendNoFollow(string path) =>
        new(OpenNoFollow(path, FILE_APPEND_DATA, OPEN_ALWAYS), FileAccess.Write);

    /// <summary>Opens an existing file for reading; refuses links and hard-linked files.</summary>
    public static FileStream OpenReadNoFollow(string path) =>
        new(OpenNoFollow(path, GENERIC_READ, OPEN_EXISTING), FileAccess.Read);

    private const uint READ_CONTROL = 0x00020000;
    private const uint WRITE_DAC = 0x00040000;

    /// <summary>
    /// Gives one of the product's own log files a protected DACL: SYSTEM and Administrators full
    /// control, nobody else anything. The file is opened by handle without following a link, and
    /// the DACL is set through that handle. Returns true when the file is protected afterwards
    /// (also when it already was), false when it does not exist.
    /// </summary>
    public static bool ProtectLogFile(string path)
    {
        if (!File.Exists(path)) { return false; }
        using var stream = new FileStream(OpenNoFollow(path, READ_CONTROL | WRITE_DAC, OPEN_EXISTING), FileAccess.Read);
        return ProtectLogFile(stream);
    }

    /// <summary>
    /// The same protection through a handle the caller already holds (opened with READ_CONTROL and
    /// WRITE_DAC): the DACL is read and set on that very file, never on a path looked up again.
    /// </summary>
    public static bool ProtectLogFile(FileStream stream)
    {
        var allowed = LogReaders();
        var current = stream.GetAccessControl();
        if (OnlyAllows(current, allowed)) { return true; }
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in allowed)
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        stream.SetAccessControl(security);
        return true;
    }

    /// <summary>
    /// SYSTEM, Administrators and the account this process runs as: SYSTEM for the service, the
    /// developer in a console or test run (who must keep access to their own log).
    /// </summary>
    internal static List<SecurityIdentifier> LogReaders()
    {
        var list = new List<SecurityIdentifier> { WellKnownSids.LocalSystem, WellKnownSids.Administrators };
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User != null && !list.Contains(identity.User)) { list.Add(identity.User); }
        return list;
    }

    /// <summary>True when a DACL is protected and allows nobody but <paramref name="allowed"/>.</summary>
    internal static bool OnlyAllows(FileSystemSecurity security, ICollection<SecurityIdentifier> allowed)
    {
        if (!security.AreAccessRulesProtected) { return false; }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) { continue; }
            if (!allowed.Contains((SecurityIdentifier)rule.IdentityReference)) { return false; }
        }
        return true;
    }

    // --- Folders the product writes to without owning them --------------------------------------

    // .NET does not map generic rights in access entries: GENERIC_ALL (0x10000000) and
    // GENERIC_WRITE (0x40000000) must be treated as write rights too.
    private const FileSystemRights WriteLikeRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes |
        (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;

    /// <summary>
    /// Returns null when a folder owned by someone else (the IME Logs folder) is safe for SYSTEM to
    /// write to: a real directory without reparse points in its path, owned by SYSTEM,
    /// Administrators or TrustedInstaller, with no write-like allow entry for anyone else.
    /// </summary>
    public static string ValidateForeignFolder(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) { return "missing"; }
        if (HasReparsePointInPath(path)) { return "reparse point in path"; }
        // The folder is opened by handle (a real directory at its expected final path) and
        // its owner and DACL are read through that handle, not by a second look-up by path.
        SafeFileHandle handle;
        try { handle = NativeFile.OpenFolderNoFollow(path); }
        catch (IOException ex) { return ex.Message.TrimEnd('.'); }
        if (handle == null) { return "missing"; }
        using (handle)
        {
            return ValidateTrustedSecurity(NativeFile.ReadSecurity(handle));
        }
    }

    internal static string ValidateTrustedSecurity(FileSystemSecurity security)
    {
        var trusted = new[] { WellKnownSids.LocalSystem, WellKnownSids.Administrators, WellKnownSids.TrustedInstaller };
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner == null || !trusted.Contains(owner)) { return "unexpected owner"; }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) { continue; }
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (trusted.Contains(sid)) { continue; }
            // CREATOR OWNER only applies to objects someone was already allowed to create.
            if (sid.Equals(WellKnownSids.CreatorOwner)) { continue; }
            if ((rule.FileSystemRights & WriteLikeRights) != 0) { return "writable by other principals"; }
        }
        return null;
    }

    /// <summary>
    /// Returns null when the service's own folder can be trusted: the folder, its parents below
    /// the volume root and every file directly in it (the service, the app and their libraries) are owned by SYSTEM,
    /// Administrators or TrustedInstaller, writable by nobody else, and not links. Anyone who can
    /// replace one of these files runs code as SYSTEM at the next service start. The engine
    /// subfolder is checked before every run (<see cref="ValidateEngineTree"/>).
    /// </summary>
    public static string ValidateInstallFolder(string folder)
    {
        var reason = ValidateForeignFolder(folder);
        if (reason != null) { return $"the install folder is not protected ({reason})"; }
        // Whoever can change a parent folder (the publisher folder) can rename the install
        // folder away and put another one in its place, after this check and before a start.
        // The volume root is left out: its default ACL lets users create folders there, but not
        // rename or delete Program Files.
        reason = ValidateInstallParents(folder, d => d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
        if (reason != null) { return reason; }
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { return $"{file.Name} is a link"; }
            var r = ValidateTrustedSecurity(file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            if (r != null) { return $"{file.Name} is not protected ({r})"; }
        }
        return null;
    }

    /// <summary>The parent check of <see cref="ValidateInstallFolder"/>, with the ACL reader passed in (tests).</summary>
    internal static string ValidateInstallParents(string folder, Func<DirectoryInfo, FileSystemSecurity> readSecurity)
    {
        for (var parent = new DirectoryInfo(Path.GetFullPath(folder)).Parent; parent?.Parent != null; parent = parent.Parent)
        {
            var reason = ValidateTrustedSecurity(readSecurity(parent));
            if (reason != null) { return $"a parent of the install folder is not protected ({parent.FullName}: {reason})"; }
        }
        return null;
    }

    /// <summary>
    /// True when a file or folder can be trusted to run as SYSTEM: it exists, is not a link, is
    /// owned by SYSTEM, Administrators or TrustedInstaller, and has no allow entry with a
    /// write-like right for anyone else (CREATOR OWNER aside).
    /// </summary>
    public static bool IsAdminOnlyPath(string path)
    {
        try
        {
            FileSystemInfo item = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!item.Exists || (item.Attributes & FileAttributes.ReparsePoint) != 0) { return false; }
            var security = item is DirectoryInfo dir
                ? (FileSystemSecurity)dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : ((FileInfo)item).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return ValidateTrustedSecurity(security) == null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns null when the compiled engine can be trusted to run as SYSTEM: it exists, the
    /// install folder and every file directly in it (the engine and the libraries it loads from
    /// there) pass <see cref="ValidateInstallFolder"/>, and none of those files is a link or has
    /// more than one hard link. Checked before every run.
    /// </summary>
    public static string ValidateEngineExecutable(string executable) => CheckEngineExecutable(executable)?.Problem;

    /// <summary>
    /// The check of <see cref="ValidateEngineExecutable"/>, which also says whether the problem was
    /// a file that could not be opened (which can pass a moment later) rather than a link, a hard
    /// link or a folder that is not protected (which cannot). Null when the engine can be trusted.
    /// </summary>
    public static EngineCheck CheckEngineExecutable(string executable)
    {
        if (!File.Exists(executable)) { return new EngineCheck("the engine is missing", couldNotOpen: false); }
        var folder = Path.GetDirectoryName(Path.GetFullPath(executable));
        var reason = ValidateInstallFolder(folder);
        if (reason != null) { return new EngineCheck(reason, couldNotOpen: false); }
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            var problem = OpenProblem(file);
            if (problem != null) { return problem; }
        }
        // The engine also reads the shared IME log pattern table from the Engine folder.
        var engineFolder = Path.Combine(folder, ProductInfo.EngineFolderName);
        return Directory.Exists(engineFolder) ? CheckEngineTree(engineFolder) : null;
    }

    /// <summary>
    /// Null when the file opens without following a link and has one hard link; otherwise which of
    /// the two failed: it could not be opened (the Windows error is named), or it is a link or has
    /// more than one hard link.
    /// </summary>
    internal static EngineCheck OpenProblem(FileInfo file, Func<string, FileStream> open = null)
    {
        try
        {
            using ((open ?? OpenReadNoFollow)(file.FullName)) { }
            return null;
        }
        catch (LinkRefusedException)
        {
            return new EngineCheck($"{file.Name} is a link or has more than one hard link", couldNotOpen: false);
        }
        catch (Win32Exception ex)
        {
            return new EngineCheck($"{file.Name} could not be opened (Windows error {ex.NativeErrorCode}: {new Win32Exception(ex.NativeErrorCode).Message.TrimEnd('.')})", couldNotOpen: true);
        }
        catch (Exception ex)
        {
            return new EngineCheck($"{file.Name} could not be opened ({ex.GetType().Name})", couldNotOpen: true);
        }
    }

    /// <summary>
    /// Returns null when the engine can be trusted to run as SYSTEM: the engine folder, its parent
    /// (the install folder) and every subfolder and file below it are owned by SYSTEM,
    /// Administrators or TrustedInstaller, writable by nobody else, and contain no reparse points
    /// or hard-linked files. The service's own files are checked at start
    /// (<see cref="ValidateInstallFolder"/>); development copies fail both checks.
    /// </summary>
    public static string ValidateEngineTree(string engineFolder) => CheckEngineTree(engineFolder)?.Problem;

    internal static EngineCheck CheckEngineTree(string engineFolder)
    {
        static EngineCheck Fixed(string problem) => new(problem, couldNotOpen: false);
        var root = new DirectoryInfo(engineFolder);
        if (!root.Exists) { return Fixed("the engine folder is missing"); }
        var parent = root.Parent;
        if (parent != null)
        {
            var parentReason = ValidateForeignFolder(parent.FullName);
            if (parentReason != null) { return Fixed($"the install folder is not protected ({parentReason})"); }
        }
        var reason = ValidateForeignFolder(root.FullName);
        if (reason != null) { return Fixed($"the engine folder is not protected ({reason})"); }

        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { return Fixed($"{entry.Name} is a link"); }
            if (entry is DirectoryInfo dir)
            {
                var r = ValidateTrustedSecurity(dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
                if (r != null) { return Fixed($"{dir.Name} is not protected ({r})"); }
            }
            else if (entry is FileInfo file)
            {
                var r = ValidateTrustedSecurity(file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
                if (r != null) { return Fixed($"{file.Name} is not protected ({r})"); }
                var problem = OpenProblem(file);
                if (problem != null) { return problem; }
            }
        }
        return null;
    }
}

/// <summary>Why the engine may not run as SYSTEM, from <see cref="FileSystemSafety.CheckEngineExecutable"/>.</summary>
public sealed class EngineCheck
{
    public EngineCheck(string problem, bool couldNotOpen)
    {
        Problem = problem;
        CouldNotOpen = couldNotOpen;
    }

    /// <summary>What failed, for the service log and its event.</summary>
    public string Problem { get; }

    /// <summary>
    /// A file could not be opened (in use by another process, or refused for a moment), which can
    /// pass a moment later; false for a link, a hard link, a missing file or a folder that is not
    /// protected, which repair or reinstall fixes.
    /// </summary>
    public bool CouldNotOpen { get; }
}

/// <summary>A file was refused because it is a link or has more than one hard link.</summary>
public sealed class LinkRefusedException : IOException
{
    public LinkRefusedException() { }
    public LinkRefusedException(string message) : base(message) { }
    public LinkRefusedException(string message, Exception inner) : base(message, inner) { }
}
