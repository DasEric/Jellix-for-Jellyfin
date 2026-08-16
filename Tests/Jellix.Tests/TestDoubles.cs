namespace Jellyfin.Plugin.MediaForge.Integration;

internal sealed class JellixBridge
{
}

internal sealed class ThrowingServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType)
        => throw new InvalidOperationException("Simulated optional-plugin activation failure.");
}
