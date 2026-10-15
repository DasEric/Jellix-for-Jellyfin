using System.Reflection;
using System.Text.Json;

namespace Jellyfin.Plugin.Jellix.Integrations;

/// <summary>
/// Optional versioned bridge to the H0melab Jellyfin connector. The bridge deliberately
/// avoids reading the connector's request file or creating a second request store.
/// </summary>
public sealed class H0melabBridgeClient
{
    public const string ProtocolVersion = "1";
    private const string BridgeTypeName = "Jellyfin.Plugin.AniWorld.Integration.JellixBridge";
    private readonly IServiceProvider _services;

    public H0melabBridgeClient(IServiceProvider services)
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
            type = FindBridgeType() ?? throw new H0melabBridgeUnavailableException("The installed H0melab connector does not provide the Jellix bridge.");
            instance = _services.GetService(type) ?? throw new H0melabBridgeUnavailableException("The H0melab Jellix bridge is not registered.");
            method = type.GetMethod("InvokeAsync", BindingFlags.Instance | BindingFlags.Public, [typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(CancellationToken)])
                ?? throw new H0melabBridgeUnavailableException("The H0melab Jellix bridge has an incompatible API.");
        }
        catch (H0melabBridgeUnavailableException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new H0melabBridgeUnavailableException("The H0melab Jellix bridge could not be initialized.");
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
            throw Rejected();
        }

        try
        {
            return await AwaitResultAsync(invoked, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new H0melabBridgeException(Plugin.Instance?.Configuration.Language == "en"
                ? "The H0melab connector did not respond in time."
                : "Der H0melab-Connector hat nicht rechtzeitig geantwortet.");
        }
    }

    private static async Task<JsonDocument> AwaitResultAsync(object? invoked, CancellationToken cancellationToken)
    {
        if (invoked is not Task task)
        {
            throw new H0melabBridgeUnavailableException("The H0melab Jellix bridge returned an invalid task.");
        }

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            var json = resultProperty?.GetValue(task) as string;
            if (string.IsNullOrWhiteSpace(json) || json.Length > 2 * 1024 * 1024)
            {
                throw new H0melabBridgeException("The H0melab connector returned an invalid response.");
            }

            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (H0melabBridgeException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Rejected();
        }
    }

    private static H0melabBridgeException Rejected()
        => new(Plugin.Instance?.Configuration.Language == "en"
            ? "The H0melab connector rejected the operation. Check the connector settings and request status."
            : "Der H0melab-Connector hat die Aktion abgelehnt. Bitte Connector-Einstellungen und Anfragestatus prüfen.");

    private static Type? FindBridgeType()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic)
            .Select(assembly => assembly.GetType(BridgeTypeName, throwOnError: false, ignoreCase: false))
            .FirstOrDefault(type => type is not null);
}

public sealed class H0melabBridgeUnavailableException : Exception
{
    public H0melabBridgeUnavailableException(string message)
        : base(message)
    {
    }
}

public sealed class H0melabBridgeException : Exception
{
    public H0melabBridgeException(string message)
        : base(message)
    {
    }
}
