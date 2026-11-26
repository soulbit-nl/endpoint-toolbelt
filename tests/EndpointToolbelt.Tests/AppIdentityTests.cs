using System.Reflection;
using EndpointToolbelt.App.Ui;
using Xunit;

namespace EndpointToolbelt.Tests;

/// <summary>
/// The app's file description is its name as people read it: Windows shows it in the setup's
/// files-in-use screen (Restart Manager lists the open app by it) and in Task Manager.
/// </summary>
public class AppIdentityTests
{
    [Fact]
    public void The_app_file_description_is_Endpoint_Toolbelt_with_a_space()
    {
        var app = typeof(MainWindow).Assembly;
        Assert.Equal("Endpoint Toolbelt", app.GetCustomAttribute<AssemblyTitleAttribute>()?.Title);
        Assert.Equal("Endpoint Toolbelt", app.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }
}
