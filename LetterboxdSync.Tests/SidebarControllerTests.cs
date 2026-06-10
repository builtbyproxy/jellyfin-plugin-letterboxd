using LetterboxdSync.Api;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LetterboxdSync.Tests;

/// <summary>
/// Serves the embedded sidebar.js asset. The script is compiled into the plugin
/// assembly as an embedded resource, so the happy path returns it as JavaScript.
/// </summary>
public class SidebarControllerTests
{
    [Fact]
    public void GetSidebarJs_EmbeddedResourcePresent_ReturnsJavaScriptFile()
    {
        var controller = new SidebarController();

        var result = controller.GetSidebarJs();

        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("application/javascript", file.ContentType);
        Assert.True(file.FileStream.Length > 0, "embedded sidebar.js should not be empty");
    }
}
