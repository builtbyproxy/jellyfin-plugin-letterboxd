using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace LetterboxdSync;

/// <summary>
/// Startup task that registers with File Transformation plugin to inject
/// the Letterboxd sidebar link into index.html for all users.
/// </summary>
public class SidebarInjectionTask : IScheduledTask
{
    private readonly ILogger<SidebarInjectionTask> _logger;

    public string Name => "Letterboxd Sidebar Registration";

    public string Key => "LetterboxdSidebarInjection";

    public string Description => "Registers sidebar link with File Transformation plugin.";

    public string Category => "Letterboxd Sync";

    public SidebarInjectionTask(ILogger<SidebarInjectionTask> logger)
    {
        _logger = logger;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }
        };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(10);

        try
        {
            RegisterWithFileTransformation();
            _logger.LogInformation("Letterboxd sidebar injection registered successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Sidebar injection failed: {Error}", ex.Message);
            _logger.LogDebug(ex, "Full sidebar injection error");
        }

        progress.Report(100);
        return Task.CompletedTask;
    }

    private void RegisterWithFileTransformation()
    {
        Assembly? ftAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);

        if (ftAssembly == null)
        {
            ftAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation") ?? false);
        }

        if (ftAssembly == null)
        {
            _logger.LogDebug("File Transformation plugin not installed, skipping sidebar injection");
            return;
        }

        Type? pluginInterface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        if (pluginInterface == null)
        {
            _logger.LogWarning("File Transformation PluginInterface type not found");
            return;
        }

        var registerMethod = pluginInterface.GetMethod("RegisterTransformation");
        if (registerMethod == null)
        {
            _logger.LogWarning("RegisterTransformation method not found");
            return;
        }

        // Build the JObject using File Transformation's own Newtonsoft assembly
        // to avoid type identity mismatch between different loaded copies
        Assembly? newtonsoftAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json"
                && a != typeof(Newtonsoft.Json.Linq.JObject).Assembly);

        // Fall back to any loaded copy
        if (newtonsoftAssembly == null)
        {
            newtonsoftAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json");
        }

        if (newtonsoftAssembly == null)
        {
            _logger.LogWarning("Newtonsoft.Json assembly not found");
            return;
        }

        Type? jObjectType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JObject");
        Type? jPropertyType = newtonsoftAssembly.GetType("Newtonsoft.Json.Linq.JProperty");

        if (jObjectType == null || jPropertyType == null)
        {
            _logger.LogWarning("Could not find JObject/JProperty types in Newtonsoft.Json");
            return;
        }

        var jObj = Activator.CreateInstance(jObjectType)!;
        var addMethod = jObjectType.GetMethod("Add", new[] { typeof(object) });

        void AddProp(string name, string value)
        {
            var prop = Activator.CreateInstance(jPropertyType, name, (object)value)!;
            addMethod!.Invoke(jObj, new[] { prop });
        }

        AddProp("id", Guid.NewGuid().ToString());
        AddProp("fileNamePattern", "index.html");
        AddProp("callbackAssembly", typeof(SidebarTransformCallback).Assembly.FullName!);
        AddProp("callbackClass", typeof(SidebarTransformCallback).FullName!);
        AddProp("callbackMethod", nameof(SidebarTransformCallback.Transform));

        _logger.LogInformation("Registering sidebar transformation with File Transformation plugin");
        registerMethod.Invoke(null, new[] { jObj });
    }
}

public static class SidebarTransformCallback
{
    public static string Transform(SidebarPatchPayload payload)
    {
        var contents = payload.Contents ?? string.Empty;

        // Only transform actual HTML files, not JS chunks with "index-html" in their name
        if (!contents.Contains("</head>") || contents.Contains("LetterboxdSync/Web/sidebar.js"))
        {
            return contents;
        }

        var injection = "<script src=\"/LetterboxdSync/Web/sidebar.js\" defer></script>";
        return contents.Replace("</head>", $"{injection}\n</head>");
    }
}

public class SidebarPatchPayload
{
    public string? Contents { get; set; }
}
