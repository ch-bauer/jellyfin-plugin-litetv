using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.LiteTv.Web;

/// <summary>
/// Registers the script injection with the File Transformation plugin when it is
/// installed (same mechanism as Intro Skipper). File Transformation then applies
/// <see cref="ScriptInjector.TransformIndexHtml"/> whenever index.html is served.
/// When the plugin is not installed (or registration fails), the request-time middleware
/// (<see cref="InjectionStartupFilter"/>) takes over as fallback.
/// </summary>
public class FileTransformationRegistration : IHostedService
{
    /// <summary>
    /// Stable id identifying this transformation with the File Transformation plugin.
    /// </summary>
    private const string TransformationId = "7134777b-8b52-4f6b-a248-35d75e88cfc2";

    private readonly ILogger<FileTransformationRegistration> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationRegistration"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public FileTransformationRegistration(ILogger<FileTransformationRegistration> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether the transformation was registered with the
    /// File Transformation plugin. When true, the middleware fallback stands down.
    /// </summary>
    public static bool Registered { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var fileTransformationAssembly = AssemblyLoadContext.All
                .SelectMany(x => x.Assemblies)
                .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);

            if (fileTransformationAssembly is null)
            {
                _logger.LogInformation("File Transformation plugin not found; LiteTV uses request-time middleware injection.");
                return Task.CompletedTask;
            }

            var pluginInterfaceType = fileTransformationAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            var registerMethod = pluginInterfaceType?.GetMethod("RegisterTransformation");
            if (registerMethod is null)
            {
                _logger.LogWarning("File Transformation plugin found but its registration interface is missing; LiteTV falls back to middleware injection.");
                return Task.CompletedTask;
            }

            var payload = new JObject
            {
                { "id", TransformationId },
                { "fileNamePattern", "index.html" },
                { "callbackAssembly", GetType().Assembly.FullName },
                { "callbackClass", typeof(ScriptInjector).FullName },
                { "callbackMethod", nameof(ScriptInjector.TransformIndexHtml) }
            };

            registerMethod.Invoke(null, [payload]);
            Registered = true;
            _logger.LogInformation("LiteTV script registered with the File Transformation plugin.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not register LiteTV with the File Transformation plugin; falling back to middleware injection.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
