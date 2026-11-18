using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("EndpointToolbelt.Tests.TestRunFramework", "EndpointToolbelt.Tests")]

namespace EndpointToolbelt.Tests;

/// <summary>
/// xUnit's own test framework with one addition: after the last test of the assembly and
/// before xUnit unloads the run's application domain, the shared UI thread of the window tests
/// is shut down (<see cref="MainWindowPagesTests.StopSharedUi"/>). Left running, the unload
/// aborted it in the middle of its COM teardown and the test host never exited (hang dumps).
/// xUnit 2 has no assembly fixture; its assembly runner's "before finished" step is the hook.
/// </summary>
public sealed class TestRunFramework : XunitTestFramework
{
    public TestRunFramework(IMessageSink messageSink) : base(messageSink) { }

    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
        => new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor : XunitTestFrameworkExecutor
    {
        public Executor(AssemblyName assemblyName, ISourceInformationProvider sourceInformationProvider, IMessageSink diagnosticMessageSink)
            : base(assemblyName, sourceInformationProvider, diagnosticMessageSink) { }

        // As xUnit's own, with the assembly runner below.
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new AssemblyRunner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class AssemblyRunner : XunitTestAssemblyRunner
    {
        public AssemblyRunner(ITestAssembly testAssembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnosticMessageSink, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
            : base(testAssembly, testCases, diagnosticMessageSink, executionMessageSink, executionOptions) { }

        protected override Task BeforeTestAssemblyFinishedAsync()
        {
            MainWindowPagesTests.StopSharedUi();
            return base.BeforeTestAssemblyFinishedAsync();
        }
    }
}
