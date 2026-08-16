using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Jellix.Helpers;

/// <summary>Idempotent jellyfin-web transformation callbacks.</summary>
public static class TransformationPatches
{
    private const string PluginName = "Jellix";

    public static string IndexHtml(PatchRequestPayload content)
        => ApplyIndexHtml(content.Contents ?? string.Empty, Plugin.Instance?.Configuration.UserPageEnabled == true);

    public static string ApplyIndexHtml(string source, bool enabled)
    {
        var updated = RemoveScript(source);
        if (!enabled || !updated.Contains("</body>", StringComparison.OrdinalIgnoreCase))
        {
            return updated;
        }

        const string script = "<script plugin=\"Jellix\" src=\"../Jellix/InjectionScript\" defer></script>";
        return Regex.Replace(updated, "</body>", script + "\n</body>", RegexOptions.IgnoreCase);
    }

    public static string RemoveScript(string source)
        => Regex.Replace(source ?? string.Empty, "<script[^>]*plugin=[\"']Jellix[\"'][^>]*>\\s*</script>\\s*", string.Empty, RegexOptions.IgnoreCase);
}
