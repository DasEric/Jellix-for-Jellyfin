namespace Jellyfin.Plugin.Jellix.Discord;

internal static class DiscordText
{
    public static bool German => !string.Equals(Plugin.Instance?.Configuration.Language, "en", StringComparison.OrdinalIgnoreCase);

    public static string T(string german, string english) => German ? german : english;

    public static string Command(string german, string english) => German ? german : english;

    public static string Error(string message)
        => T("Fehler: ", "Error: ") + message;
}

