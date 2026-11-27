namespace Jellyfin.Plugin.AniWorld.Integration;

internal sealed class JellixBridge
{
    internal string Result { get; set; } = "{}";
    internal Exception? Failure { get; set; }
    internal (string Protocol, string Operation, string User, string Payload) LastCall { get; private set; }

    public Task<string> InvokeAsync(string protocol, string operation, string userId, string username, string payload, CancellationToken cancellationToken)
    {
        LastCall = (protocol, operation, userId, payload);
        return Failure is null ? Task.FromResult(Result) : Task.FromException<string>(Failure);
    }
}

/// <summary>Same shape as the connector's user-facing rejection (matched by type name).</summary>
internal sealed class AniWorldApplicationException(System.Net.HttpStatusCode statusCode, string message) : Exception(message)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}

internal sealed class BridgeServiceProvider(JellixBridge bridge) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType == typeof(JellixBridge) ? bridge : null;
}

internal sealed class ThrowingServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType)
        => throw new InvalidOperationException("Simulated optional-plugin activation failure.");
}
