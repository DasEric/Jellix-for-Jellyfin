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
            // exception because it could contain an upstream response or secret;
            // only whitelisted user-facing reasons are translated.
            throw Rejected(exception.InnerException);
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
        catch (Exception exception)
        {
            throw Rejected(exception);
        }
    }

    private static H0melabBridgeException Rejected(Exception? reason = null)
    {
        var english = Plugin.Instance?.Configuration.Language == "en";
        return new(DescribeRejection(reason, english) ?? (english
            ? "The request system rejected the operation. Please try again later or contact the admin."
            : "Das Anfragesystem hat die Aktion abgelehnt. Bitte später erneut versuchen oder den Admin kontaktieren."));
    }

    /// <summary>Maps the AniWorld Requests connector's known, user-facing rejections (2.x) to Jellix texts.</summary>
    internal static string? DescribeRejection(Exception? reason, bool english)
    {
        if (reason is null || reason.GetType().Name != "AniWorldApplicationException") return null;
        var status = reason.GetType().GetProperty("StatusCode")?.GetValue(reason) as System.Net.HttpStatusCode?;
        if (status == System.Net.HttpStatusCode.TooManyRequests)
            return english ? "Too many requests. Please wait a moment." : "Zu viele Anfragen. Bitte kurz warten.";
        if (status == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            // Maintenance mode: the admin-written notice is meant for users.
            var notice = new string(reason.Message.Where(character => !char.IsControl(character)).Take(300).ToArray()).Trim();
            return notice.Length > 0 ? notice : english ? "Requests are paused for maintenance." : "Anfragen sind wegen Wartung pausiert.";
        }

        return reason.Message switch
        {
            "This content already has an open request." => english ? "You already have an open request for this title." : "Du hast für diesen Titel bereits eine offene Anfrage.",
            "The user request limit has been reached." => english ? "You have reached the maximum number of open requests." : "Du hast die maximale Anzahl offener Anfragen erreicht.",
            "The request store is currently full." => english ? "The request system is full right now. Please try again later." : "Das Anfragesystem ist gerade voll. Bitte später erneut versuchen.",
            "The selected content is already available." => english ? "This title is already available on Jellyfin." : "Dieser Titel ist bereits in Jellyfin verfügbar.",
            "The Jellyfin user does not exist or is disabled." => english ? "Your Jellyfin account is unavailable or disabled." : "Dein Jellyfin-Konto ist nicht verfügbar oder deaktiviert.",
            "Die Auswahl ist abgelaufen, wurde bereits verwendet oder gehört zu einem anderen Benutzer." => english ? "This selection expired or was already used. Please search again." : "Diese Auswahl ist abgelaufen oder wurde schon verwendet. Bitte erneut suchen.",
            "Diese Kategorie ist in den Admin-Einstellungen deaktiviert." => english ? "Requests for this category are disabled." : "Anfragen für diese Kategorie sind deaktiviert.",
            "Die ausgewählte AniWorld-Quelle ist für diesen Medientyp nicht freigegeben oder deaktiviert." => english ? "No source is enabled for this media type." : "Für diesen Medientyp ist keine Quelle freigegeben.",
            _ => null,
        };
    }

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
