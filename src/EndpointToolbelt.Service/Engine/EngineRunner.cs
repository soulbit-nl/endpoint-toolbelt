using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace EndpointToolbelt.Service.Engine;

public sealed class EngineStartInfo
{
    public string FileName { get; set; }
    public IList<string> Arguments { get; set; } = new List<string>();
    public string WorkingDirectory { get; set; }
    public IDictionary<string, string> Environment { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan TimeLimit { get; set; }
}

public enum EngineOutcome
{
    Exited,
    TimedOut,
    Cancelled,
    FailedToStart,
}

public sealed class EngineRunResult
{
    public EngineOutcome Outcome { get; set; }
    public int? ExitCode { get; set; }
    public string StartError { get; set; }

    /// <summary>The Win32 error CreateProcess gave when the engine did not start; 0 otherwise.</summary>
    public int StartErrorCode { get; set; }

    /// <summary>Last lines of standard error (capped).</summary>
    public List<string> ErrorTail { get; } = new();

    /// <summary>Standard output lines that were not records (capped).</summary>
    public List<string> OtherOutputTail { get; } = new();
}

public interface IEngineRunner
{
    /// <summary>
    /// Runs the engine and calls <paramref name="onRecord"/> for every standard-output line that
    /// is a JSON object. Blocks until the process tree has ended.
    /// </summary>
    EngineRunResult Run(EngineStartInfo startInfo, Action<string> onRecord, CancellationToken cancellation);
}

/// <summary>
/// Starts the engine suspended, puts it into a Job Object with kill-on-close, then resumes it.
/// The whole process tree (powershell, dism, DismHost, sfc) dies on a timeout or cancellation.
/// Work done inside Windows services (TrustedInstaller, Windows Update) is outside the job and
/// may continue; callers report that case.
/// </summary>
public sealed class JobObjectEngineRunner : IEngineRunner
{
    private const int TailLines = 50;

    /// <summary>Longest output line accepted; the rest of a longer line is dropped.</summary>
    public const int MaxLineChars = 64 * 1024;

    public EngineRunResult Run(EngineStartInfo startInfo, Action<string> onRecord, CancellationToken cancellation)
    {
        var result = new EngineRunResult();
        using var job = NativeJob.Create();

        SafeFileHandle outRead = null, outWrite = null, errRead = null, errWrite = null, nulIn = null;
        var processInfo = default(NativeProcess.PROCESS_INFORMATION);
        var environment = IntPtr.Zero;
        try
        {
            NativeProcess.CreateInheritablePipe(out outRead, out outWrite);
            NativeProcess.CreateInheritablePipe(out errRead, out errWrite);
            nulIn = NativeProcess.OpenNulForInheritance();

            environment = NativeProcess.BuildEnvironmentBlock(startInfo.Environment);
            var commandLine = NativeProcess.BuildCommandLine(startInfo.FileName, startInfo.Arguments);

            if (!NativeProcess.Start(startInfo.FileName, commandLine, environment, startInfo.WorkingDirectory,
                    nulIn, outWrite, errWrite, out processInfo, out var startError, out var startErrorCode))
            {
                result.Outcome = EngineOutcome.FailedToStart;
                result.StartError = startError;
                result.StartErrorCode = startErrorCode;
                return result;
            }

            // Assign before the first instruction runs, so no child can escape the job.
            if (!job.Assign(processInfo.hProcess))
            {
                var error = Marshal.GetLastWin32Error();
                NativeProcess.TerminateProcess(processInfo.hProcess, 1);
                result.Outcome = EngineOutcome.FailedToStart;
                result.StartError = new Win32Exception(error).Message;
                return result;
            }
            // (DWORD)-1 means the thread was not resumed: the engine would never run.
            if (NativeProcess.ResumeThread(processInfo.hThread) == uint.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                NativeProcess.TerminateProcess(processInfo.hProcess, 1);
                result.Outcome = EngineOutcome.FailedToStart;
                result.StartError = new Win32Exception(error).Message;
                return result;
            }

            // Our copies of the child's pipe ends must be closed, or the readers never see EOF.
            outWrite.Dispose(); outWrite = null;
            errWrite.Dispose(); errWrite = null;
            nulIn.Dispose(); nulIn = null;

            var outReader = StartReader(outRead, line =>
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal))
                {
                    onRecord(trimmed);
                }
                else if (trimmed.Length > 0)
                {
                    AddTail(result.OtherOutputTail, trimmed);
                }
            });
            var errReader = StartReader(errRead, line => AddTail(result.ErrorTail, line));

            // A monotonic clock, so a clock change does not shorten or extend the limit.
            var clock = Stopwatch.StartNew();
            while (true)
            {
                if (NativeProcess.WaitForExit(processInfo.hProcess, 250))
                {
                    result.Outcome = EngineOutcome.Exited;
                    result.ExitCode = NativeProcess.GetExitCode(processInfo.hProcess);
                    break;
                }
                if (cancellation.IsCancellationRequested)
                {
                    job.Terminate();
                    result.Outcome = EngineOutcome.Cancelled;
                    break;
                }
                if (clock.Elapsed >= startInfo.TimeLimit)
                {
                    job.Terminate();
                    result.Outcome = EngineOutcome.TimedOut;
                    break;
                }
            }

            // Children that are still alive hold the pipes open; ending the job ends them, and only
            // processes in the job hold the write ends, so the readers then reach end of stream.
            // The run does not return while a reader could still deliver records.
            if (!outReader.Join(TimeSpan.FromSeconds(10)) || !errReader.Join(TimeSpan.FromSeconds(2)))
            {
                job.Terminate();
                outReader.Join();
                errReader.Join();
            }
            return result;
        }
        finally
        {
            outWrite?.Dispose(); errWrite?.Dispose(); nulIn?.Dispose();
            outRead?.Dispose(); errRead?.Dispose();
            NativeProcess.CloseHandle(processInfo.hThread);
            NativeProcess.CloseHandle(processInfo.hProcess);
            if (environment != IntPtr.Zero) { Marshal.FreeHGlobal(environment); }
        }
    }

    private static void AddTail(List<string> tail, string line)
    {
        lock (tail)
        {
            tail.Add(line.Length > 1000 ? line.Substring(0, 1000) : line);
            if (tail.Count > TailLines) { tail.RemoveAt(0); }
        }
    }

    private static Thread StartReader(SafeFileHandle handle, Action<string> onLine)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
                // The engine contract: standard output is UTF-8.
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                ReadLines(reader, MaxLineChars, onLine);
            }
            catch (IOException) { /* pipe closed */ }
            catch (ObjectDisposedException) { }
            catch (Exception) { /* never let a reader thread take down the service */ }
        })
        { IsBackground = true, Name = "EngineOutputReader" };
        thread.Start();
        return thread;
    }

    /// <summary>Reads lines without ever buffering more than <paramref name="maxChars"/> of one line.</summary>
    internal static void ReadLines(TextReader reader, int maxChars, Action<string> onLine)
    {
        var sb = new StringBuilder();
        var overflow = false;
        int ch;
        while ((ch = reader.Read()) != -1)
        {
            if (ch == '\n')
            {
                if (!overflow) { onLine(sb.ToString().TrimEnd('\r')); }
                sb.Clear();
                overflow = false;
                continue;
            }
            if (overflow) { continue; }
            if (sb.Length >= maxChars)
            {
                // Over-long lines are dropped entirely; a cut-off record would be invalid JSON anyway.
                overflow = true;
                sb.Clear();
                continue;
            }
            sb.Append((char)ch);
        }
        if (!overflow && sb.Length > 0) { onLine(sb.ToString().TrimEnd('\r')); }
    }
}

internal sealed class NativeJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    private IntPtr _handle;

    private NativeJob(IntPtr handle) => _handle = handle;

    public static NativeJob Create()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            NativeProcess.CloseHandle(handle);
            throw new Win32Exception(error);
        }
        return new NativeJob(handle);
    }

    public bool Assign(IntPtr process) => AssignProcessToJobObject(_handle, process);

    public void Terminate() => TerminateJobObject(_handle, 1);

    public void Dispose()
    {
        // Closing the last handle kills every process still in the job.
        if (_handle != IntPtr.Zero)
        {
            NativeProcess.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

internal static class NativeProcess
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint HANDLE_FLAG_INHERIT = 0x1;
    private const uint INFINITE_WAIT_OBJECT_0 = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SECURITY_ATTRIBUTES attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandleImport(IntPtr handle);

    public static void CloseHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero) { CloseHandleImport(handle); }
    }

    public static void CreateInheritablePipe(out SafeFileHandle read, out SafeFileHandle write)
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out read, out write, ref sa, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        // Only the child's end is inheritable.
        if (!SetHandleInformation(read, HANDLE_FLAG_INHERIT, 0)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    public static SafeFileHandle OpenNulForInheritance()
    {
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        var handle = CreateFile("NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref sa, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }

    public static IntPtr BuildEnvironmentBlock(IDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var pair in environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.IndexOf('=') >= 0 || pair.Key.IndexOf('\0') >= 0 || (pair.Value ?? "").IndexOf('\0') >= 0)
            {
                throw new ArgumentException("Invalid environment variable.");
            }
            sb.Append(pair.Key).Append('=').Append(pair.Value ?? "").Append('\0');
        }
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    private static readonly char[] QuoteTriggers = { ' ', '\t', '\n', '\v', '"' };

    /// <summary>Quotes arguments for CommandLineToArgvW (the rules powershell.exe uses).</summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(QuoteTriggers) < 0) { return argument; }
        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string BuildCommandLine(string fileName, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { QuoteArgument(fileName) }.Concat(arguments.Select(QuoteArgument)));

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = new(0x00020002);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessEx(string applicationName, [In, Out] char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    /// <summary>
    /// Starts the process suspended. Only the three standard handles are inherited
    /// (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), whatever else in the service is inheritable.
    /// </summary>
    public static bool Start(string fileName, string commandLine, IntPtr environment, string workingDirectory,
        SafeFileHandle stdIn, SafeFileHandle stdOut, SafeFileHandle stdErr, out PROCESS_INFORMATION info, out string error, out int errorCode)
    {
        var handles = new[] { stdIn.DangerousGetHandle(), stdOut.DangerousGetHandle(), stdErr.DangerousGetHandle() };
        var handleArray = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
        var attributeList = IntPtr.Zero;
        var listInitialized = false;
        try
        {
            Marshal.Copy(handles, 0, handleArray, handles.Length);
            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            listInitialized = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleArray,
                    new IntPtr(IntPtr.Size * handles.Length), IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var si = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    hStdInput = handles[0],
                    hStdOutput = handles[1],
                    hStdError = handles[2],
                },
                lpAttributeList = attributeList,
            };
            // CreateProcessW may write to the command line buffer: a writable, terminated copy.
            var ok = CreateProcessEx(fileName, (commandLine + "\0").ToCharArray(), IntPtr.Zero, IntPtr.Zero, true,
                CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT,
                environment, workingDirectory, ref si, out info);
            errorCode = ok ? 0 : Marshal.GetLastWin32Error();
            error = ok ? null : new Win32Exception(errorCode).Message;
            return ok;
        }
        finally
        {
            if (listInitialized) { DeleteProcThreadAttributeList(attributeList); }
            if (attributeList != IntPtr.Zero) { Marshal.FreeHGlobal(attributeList); }
            Marshal.FreeHGlobal(handleArray);
            GC.KeepAlive(stdIn); GC.KeepAlive(stdOut); GC.KeepAlive(stdErr);
        }
    }

    public static bool WaitForExit(IntPtr process, int milliseconds) =>
        WaitForSingleObject(process, (uint)milliseconds) == INFINITE_WAIT_OBJECT_0;

    public static int GetExitCode(IntPtr process) =>
        GetExitCodeProcess(process, out var code) ? unchecked((int)code) : -1;
}
