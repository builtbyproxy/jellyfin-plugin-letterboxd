using LetterboxdSync;
using Xunit;

namespace LetterboxdSync.Tests;

public class SidebarTransformTests
{
    [Fact]
    public void Transform_InjectsScriptBeforeHeadClose()
    {
        var payload = new SidebarPatchPayload
        {
            Contents = "<html><head><title>Jellyfin</title></head><body></body></html>"
        };

        var result = SidebarTransformCallback.Transform(payload);

        Assert.Contains("<script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script>", result);
        Assert.Contains("</head>", result);
        // Script should appear before </head>
        var scriptIdx = result.IndexOf("sidebar.js");
        var headIdx = result.IndexOf("</head>");
        Assert.True(scriptIdx < headIdx);
    }

    [Fact]
    public void Transform_DoesNotDoubleInject()
    {
        var payload = new SidebarPatchPayload
        {
            Contents = "<html><head><script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script></head><body></body></html>"
        };

        var result = SidebarTransformCallback.Transform(payload);

        // Should return content unchanged
        Assert.Equal(payload.Contents, result);
    }

    [Fact]
    public void Transform_SkipsNonHtmlContent()
    {
        var jsContent = "\"use strict\";(self.webpackChunk=self.webpackChunk||[]).push([[17244],{session-login-index-html:function(){}}]);";
        var payload = new SidebarPatchPayload { Contents = jsContent };

        var result = SidebarTransformCallback.Transform(payload);

        // JS content without </head> should pass through unchanged
        Assert.Equal(jsContent, result);
        Assert.DoesNotContain("sidebar.js", result);
    }

    [Fact]
    public void Transform_SkipsJsChunkWithIndexHtmlInName()
    {
        // This was the actual bug: JS chunks like "session-login-index-html.chunk.js"
        // were being matched and corrupted with HTML injection
        var jsContent = "(self.webpackChunk=self.webpackChunk||[]).push([[17244],{14999:function(t,e,a){a.r(e)}}]);";
        var payload = new SidebarPatchPayload { Contents = jsContent };

        var result = SidebarTransformCallback.Transform(payload);

        Assert.Equal(jsContent, result);
        Assert.DoesNotContain("<script", result);
    }

    [Fact]
    public void Transform_HandlesNullContents()
    {
        var payload = new SidebarPatchPayload { Contents = null };

        var result = SidebarTransformCallback.Transform(payload);

        // Should return empty string, not throw
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Transform_HandlesEmptyContents()
    {
        var payload = new SidebarPatchPayload { Contents = string.Empty };

        var result = SidebarTransformCallback.Transform(payload);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Transform_PreservesExistingContent()
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Jellyfin</title><script src=\"/Moonfin/Web/loader.js\" defer></script></head><body><div id=\"reactRoot\"></div></body></html>";
        var payload = new SidebarPatchPayload { Contents = html };

        var result = SidebarTransformCallback.Transform(payload);

        // Original content should be preserved
        Assert.Contains("Moonfin/Web/loader.js", result);
        Assert.Contains("<meta charset=\"utf-8\">", result);
        Assert.Contains("<div id=\"reactRoot\">", result);
        // Our script should be added
        Assert.Contains("LetterboxdSync/Web/sidebar.js", result);
    }

    [Fact]
    public void Transform_RealWorldIndexHtml_InjectsCorrectly()
    {
        // Simulate the actual Jellyfin index.html structure
        var html = "<!doctype html><html class=\"preload\" dir=\"ltr\"><head><meta charset=\"utf-8\">"
            + "<script defer src=\"main.jellyfin.bundle.js\"></script>"
            + "<style>.headerMoonfinButton{opacity:.6}</style>"
            + "<script src=\"/Moonfin/Web/loader.js\" defer></script>"
            + "</head><body dir=\"ltr\"><div id=\"reactRoot\"></div></body></html>";

        var payload = new SidebarPatchPayload { Contents = html };

        var result = SidebarTransformCallback.Transform(payload);

        // Count script tags - should have original 2 + our 1
        var scriptCount = result.Split("<script").Length - 1;
        Assert.Equal(3, scriptCount);

        // Our injection should be right before </head>
        var sidebarIdx = result.IndexOf("LetterboxdSync/Web/sidebar.js");
        var headCloseIdx = result.IndexOf("</head>");
        Assert.True(sidebarIdx > 0);
        Assert.True(sidebarIdx < headCloseIdx);
    }
}

public class SidebarPatchPayloadTests
{
    [Fact]
    public void Contents_DefaultsToNull()
    {
        var payload = new SidebarPatchPayload();
        Assert.Null(payload.Contents);
    }

    [Fact]
    public void Contents_CanBeSetAndRead()
    {
        var payload = new SidebarPatchPayload { Contents = "test content" };
        Assert.Equal("test content", payload.Contents);
    }
}
