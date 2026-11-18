using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EndpointToolbelt.Tests
{
    /// <summary>
    /// Runs once when the test assembly loads. Many tests run in parallel and block their thread
    /// (waiting for a process or a gate), and the thread pool adds threads only slowly; background
    /// work that the product schedules on the pool (the device readout, reading a child process's
    /// output) then waited longer than the tests' 10-second limits on a busy machine. A higher
    /// minimum lets that work start at once, as it does in the service.
    /// </summary>
    internal static class TestAssemblySetup
    {
        [ModuleInitializer]
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "A test assembly has no entry point of its own; this must run before any test.")]
        internal static void RaiseThreadPoolMinimum()
        {
            ThreadPool.GetMinThreads(out var worker, out var io);
            ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
        }
    }
}

namespace System.Runtime.CompilerServices
{
    // .NET Framework 4.8 lacks the attribute; the compiler only needs it to exist.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
