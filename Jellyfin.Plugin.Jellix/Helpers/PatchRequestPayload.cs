namespace Jellyfin.Plugin.Jellix.Helpers;

/// <summary>Payload supplied by the optional File Transformation plugin.</summary>
public sealed class PatchRequestPayload
{
    public string? Contents { get; set; }
}
