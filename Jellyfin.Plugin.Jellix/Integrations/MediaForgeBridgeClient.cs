using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.Jellix.Integrations;

/// <summary>
/// Optional versioned bridge to MediaForge Requests. The bridge deliberately
/// avoids reading the connector's request file or creating a second request store.
/// </summary>
public sealed class MediaForgeBridgeClient
{
    public const string ProtocolVersion = "1";
    private const string BridgeTypeName = "Jellyfin.Plugin.MediaForge.Integration.JellixBridge";
    private readonly IServiceProvider _services;

    public MediaForgeBridgeClient(IServiceProvider services)
    {
        _services = services;
    }

    public static bool TypeAvailable => FindBridgeType() is not null;

    public bool IsAvailable
    {
        get
        {
            try
            {
                return FindBridgeType() is { } type && _services.GetService(type) is not null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public async Task<JsonDocument> InvokeAsync(string operation, Guid jellyfinUserId, string username, object? payload, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Type type;
        object instance;
        MethodInfo method;
        try
        {
            type = FindBridgeType() ?? throw new MediaForgeBridgeUnavailableException("The installed MediaForge connector does not provide the Jellix bridge.");
            instance = _services.GetService(type) ?? throw new MediaForgeBridgeUnavailableException("The MediaForge Jellix bridge is not registered.");
            method = type.GetMethod("InvokeAsync", BindingFlags.Instance | BindingFlags.Public, [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(CancellationToken)])
                ?? throw new MediaForgeBridgeUnavailableException("The MediaForge Jellix bridge has an incompatible API.");
        }
        catch (MediaForgeBridgeUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MediaForgeBridgeUnavailableException("The MediaForge Jellix bridge could not be initialized.");
        }
        var payloadJson = JsonSerializer.Serialize(payload ?? new { });
        object? invoked;
        try
        {
            invoked = method.Invoke(instance, [ProtocolVersion, operation, jellyfinUserId.ToString("N"), username, payloadJson, timeout.Token]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            // The connector owns error sanitization. Do not retain its internal
            // exception because it could contain an upstream response or secret.
            throw new MediaForgeBridgeException(Plugin.Instance?.Configuration.Language == "en"
                ? "MediaForge rejected the operation."
                : "MediaForge hat die Aktion abgelehnt.");
        }

        try
        {
            return await AwaitResultAsync(invoked, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaForgeBridgeException(Plugin.Instance?.Configuration.Language == "en"
                ? "MediaForge did not respond in time."
                : "MediaForge hat nicht rechtzeitig geantwortet.");
        }
    }

    private static async Task<JsonDocument> AwaitResultAsync(object? invoked, CancellationToken cancellationToken)
    {
        if (invoked is not Task task)
        {
            throw new MediaForgeBridgeUnavailableException("The MediaForge Jellix bridge returned an invalid task.");
        }

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            var json = resultProperty?.GetValue(task) as string;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 2 * 1024 * 1024)
            {
                throw new MediaForgeBridgeException("MediaForge returned an invalid response.");
            }

            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MediaForgeBridgeException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new MediaForgeBridgeException(Plugin.Instance?.Configuration.Language == "en"
                ? "MediaForge rejected the operation."
                : "MediaForge hat die Aktion abgelehnt.");
        }
    }

    private static Type? FindBridgeType()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .Select(assembly => assembly.GetType(BridgeTypeName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null);
}

public sealed class MediaForgeBridgeUnavailableException : Exception
{
    public MediaForgeBridgeUnavailableException(string message)
        : base(message)
    {
    }
}

public sealed class MediaForgeBridgeException : Exception
{
    public MediaForgeBridgeException(string message)
        : base(message)
    {
    }
}
