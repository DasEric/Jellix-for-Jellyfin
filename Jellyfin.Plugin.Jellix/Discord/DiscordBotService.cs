using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Integrations;
using Jellyfin.Plugin.Jellix.Models;
using Jellyfin.Plugin.Jellix.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Discord;

/// <summary>Discord gateway, commands, persistent outbound queue and stickies.</summary>
[SuppressMessage("Globalization", "CA1305", Justification = "Discord output intentionally follows the configured server language and culture.")]
public sealed class DiscordBotService : BackgroundService
{
    private static readonly TimeSpan InteractionRateWindow = TimeSpan.FromMinutes(1);
    private static readonly Action<ILogger, string, Exception?> LogDiscord = LoggerMessage.Define<string>(
        LogLevel.Information,
        new EventId(2001, "Discord"),
        "Discord: {Message}");
    private static readonly Action<ILogger, string, Exception?> LogDiscordWarning = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(2002, "DiscordWarning"),
        "Discord warning: {Message}");
    private readonly JellixDatabase _database;
    private readonly AccountLinkService _linkService;
    private readonly PasswordTicketService _passwordTickets;
    private readonly MediaForgeBridgeClient _mediaForge;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly OperationRateLimiter _rateLimiter;
    private readonly ILogger<DiscordBotService> _logger;
    private readonly DiscordSocketClient _client;
    private readonly Dictionary<ulong, CancellationTokenSource> _stickyDebounce = [];
    private readonly ConcurrentDictionary<ulong, byte> _expectedStickyDeletes = new();
    private readonly ConcurrentDictionary<ulong, SemaphoreSlim> _stickyRefreshLocks = new();
    private readonly object _stickySync = new();
    private readonly object _cpuSync = new();
    private readonly SemaphoreSlim _accessDecisionLock = new(1, 1);
    private DateTime _lastCpuSampleUtc = DateTime.UtcNow;
    private TimeSpan _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;
    private string[] _configurationIssues = [];
    private bool _started;
    private bool _databaseReady;

    public DiscordBotService(
        JellixDatabase database,
        AccountLinkService linkService,
        PasswordTicketService passwordTickets,
        MediaForgeBridgeClient mediaForge,
        IUserManager userManager,
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IServerApplicationHost applicationHost,
        OperationRateLimiter rateLimiter,
        ILogger<DiscordBotService> logger)
    {
        _database = database;
        _linkService = linkService;
        _passwordTickets = passwordTickets;
        _mediaForge = mediaForge;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _applicationHost = applicationHost;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 50,
            LogGatewayIntentWarnings = false,
        });
        _client.Log += OnDiscordLogAsync;
        _client.Ready += OnReadyAsync;
        _client.SlashCommandExecuted += OnSlashCommandAsync;
        _client.ButtonExecuted += OnButtonAsync;
        _client.ModalSubmitted += OnModalSubmittedAsync;
        _client.SelectMenuExecuted += OnSelectMenuAsync;
        _client.MessageCommandExecuted += OnMessageCommandAsync;
        _client.MessageReceived += OnMessageReceivedAsync;
        _client.MessageDeleted += OnMessageDeletedAsync;
    }

    public DiscordSocketClient Client => _client;

    public bool IsReady => _client.ConnectionState == ConnectionState.Connected;

    public IReadOnlyList<string> ConfigurationIssues => _configurationIssues;

    public string? ResolveOwnerDestination()
    {
        var config = Plugin.Instance?.Configuration;
        return config?.AdminAlertMode == "channel" && ulong.TryParse(config.AdminAlertChannelId, out _)
            ? "channel:" + config.AdminAlertChannelId
            : ulong.TryParse(config?.GuildId, out var guildId) && _client.GetGuild(guildId) is { } guild
                ? "dm:" + guild.OwnerId.ToString(CultureInfo.InvariantCulture)
                : null;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _databaseReady = true;
        }
        catch (Exception exception)
        {
            _configurationIssues = [DiscordText.T("Jellix-Datenbank konnte nicht geöffnet werden.", "The Jellix database could not be opened.")];
            LogDiscordWarning(_logger, "database initialization failed; Discord bot disabled", exception);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var config = Plugin.Instance?.Configuration;
        var token = Plugin.Instance?.Secrets.GetToken();
        if (config?.BotEnabled == true && !string.IsNullOrWhiteSpace(token))
        {
            try
            {
                await _client.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
                await _client.StartAsync().ConfigureAwait(false);
                _started = true;
            }
            catch (Exception exception)
            {
                _configurationIssues = [DiscordText.T("Discord-Anmeldung fehlgeschlagen.", "Discord login failed.")];
                LogDiscordWarning(_logger, "Discord login failed; Jellix will continue without the bot", exception);
                try { await _client.StopAsync().ConfigureAwait(false); } catch (Exception cleanupException) { LogDiscordWarning(_logger, "Discord cleanup after failed login failed", cleanupException); }
                try { await _client.LogoutAsync().ConfigureAwait(false); } catch (Exception cleanupException) { LogDiscordWarning(_logger, "Discord logout after failed login failed", cleanupException); }
            }
        }
        else if (config?.BotEnabled == true)
        {
            _configurationIssues = [DiscordText.T("Discord-Token fehlt oder kann nicht entschlüsselt werden.", "The Discord token is missing or cannot be decrypted.")];
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stickySync)
        {
            foreach (var value in _stickyDebounce.Values)
            {
                value.Cancel();
                value.Dispose();
            }

            _stickyDebounce.Clear();
        }

        if (_started)
        {
            try
            {
                await _client.StopAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogDiscordWarning(_logger, "Discord shutdown failed", exception);
            }

            try
            {
                await _client.LogoutAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogDiscordWarning(_logger, "Discord logout failed", exception);
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _accessDecisionLock.Dispose();
        foreach (var value in _stickyRefreshLocks.Values) value.Dispose();
        _client.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_databaseReady) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsReady)
                {
                    var job = await _database.GetNextNotificationAsync(stoppingToken).ConfigureAwait(false);
                    if (job is not null)
                    {
                        await SendNotificationAsync(job, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogDiscordWarning(_logger, "outbound queue failed", exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private Task OnDiscordLogAsync(LogMessage message)
    {
        var safe = message.Message?.Replace(Plugin.PluginGuid, "[id]", StringComparison.Ordinal) ?? message.Source;
        if (message.Severity <= LogSeverity.Warning)
        {
            LogDiscordWarning(_logger, safe, message.Exception);
        }
        else
        {
            LogDiscord(_logger, safe, message.Exception);
        }

        return Task.CompletedTask;
    }

    private async Task OnReadyAsync()
    {
        try
        {
            await SynchronizeCommandsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "command synchronization failed", exception);
        }

        ValidateConfiguration();
        try
        {
            await RestoreStickiesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "sticky restoration failed", exception);
        }

        try
        {
            await _database.SetBotStateAsync("discord-ready-utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "Discord ready state could not be persisted", exception);
        }
    }

    private async Task SynchronizeCommandsAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }
        if (!ulong.TryParse(config.GuildId, out var guildId))
        {
            LogDiscordWarning(_logger, "Guild ID is not configured", null);
            return;
        }

        var guild = _client.GetGuild(guildId);
        if (guild is null)
        {
            LogDiscordWarning(_logger, "configured guild is unavailable", null);
            return;
        }

        var guildStateKey = "command-schema-guild";
        var previousGuildValue = await _database.GetBotStateAsync(guildStateKey, cancellationToken).ConfigureAwait(false);
        if (ulong.TryParse(previousGuildValue, out var previousGuildId) && previousGuildId != guildId && _client.GetGuild(previousGuildId) is { } previousGuild)
        {
            try
            {
                await previousGuild.BulkOverwriteApplicationCommandAsync(Array.Empty<ApplicationCommandProperties>()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogDiscordWarning(_logger, "commands in the previously configured guild could not be removed", exception);
            }
        }

        var commands = BuildCommands(_mediaForge.IsAvailable);
        var expectedNames = commands.Select(value => value.Name.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        var schema = "schema-v3|" + typeof(Plugin).Assembly.GetName().Version + "|" + string.Join('|', expectedNames) + "|" + config.Language + "|" + config.MediaForgeEnabled + "|" + _mediaForge.IsAvailable;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema)));
        var existingHash = await _database.GetBotStateAsync("command-schema-hash", cancellationToken).ConfigureAwait(false);
        var existing = await guild.GetApplicationCommandsAsync().ConfigureAwait(false);
        var existingNames = existing.Select(value => value.Name).Order(StringComparer.Ordinal).ToArray();
        if (hash == existingHash && expectedNames.SequenceEqual(existingNames, StringComparer.Ordinal))
        {
            await _database.SetBotStateAsync(guildStateKey, guildId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            return;
        }

        await guild.BulkOverwriteApplicationCommandAsync(commands.ToArray()).ConfigureAwait(false);
        await _database.SetBotStateAsync("command-schema-hash", hash, cancellationToken).ConfigureAwait(false);
        await _database.SetBotStateAsync(guildStateKey, guildId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    private void ValidateConfiguration()
    {
        var config = Plugin.Instance?.Configuration;
        var issues = new List<string>();
        if (config is null || !ulong.TryParse(config.GuildId, out var guildId) || _client.GetGuild(guildId) is not { } guild)
        {
            _configurationIssues = ["Discord-Server nicht gefunden."];
            return;
        }

        foreach (var role in new[]
        {
            ("Streaming-Rolle", config.StreamingRoleId),
            ("Anfragen-Rolle", config.RequestRoleId),
            ("Admin-Rolle", config.AdminRoleId),
        })
        {
            if (!string.IsNullOrWhiteSpace(role.Item2) && (!ulong.TryParse(role.Item2, out var id) || guild.GetRole(id) is null)) issues.Add(role.Item1 + " nicht gefunden.");
        }

        var channels = new List<(string Name, string Id, bool Required)>
        {
            ("Achievement-Kanal", config.AchievementChannelId, config.AchievementsEnabled && config.AchievementNotificationMode == "channel"),
            ("Anfragen-Kanal", config.RequestNotificationChannelId, config.MediaForgeEnabled && config.RequestNotificationMode == "channel"),
            ("Neuigkeiten-Kanal", config.NewMediaChannelId, config.NewMediaNotificationsEnabled || config.NewEpisodeNotificationsEnabled),
            ("Fallback-Kanal für Zugangsanfragen", config.AccessRequestChannelId, false),
            ("Warnungs-Kanal", config.AdminAlertChannelId, config.AdminAlertsEnabled && config.AdminAlertMode == "channel"),
        };
        foreach (var channel in channels.Where(static value => value.Required))
        {
            if (!ulong.TryParse(channel.Id, out var id) || _client.GetChannel(id) is not IMessageChannel) issues.Add(channel.Name + " nicht gefunden.");
        }

        if (config.PasswordChangeEnabled && !PasswordTicketService.IsPublicUrlConfigured(config.JellyfinPublicUrl)) issues.Add("Öffentliche Jellyfin-URL fehlt oder ist unsicher.");
        if (config.AccessRequestsEnabled && !config.PasswordChangeEnabled) issues.Add("Zugangsanfragen benötigen die Passwortänderung.");
        if (config.AccessRequestsEnabled && guild.OwnerId == 0) issues.Add("Discord-Server-Owner konnte nicht ermittelt werden.");
        if (config.MediaForgeEnabled && !_mediaForge.IsAvailable) issues.Add("MediaForge-Jellix-Brücke fehlt.");
        _configurationIssues = issues.ToArray();
        foreach (var issue in issues) LogDiscordWarning(_logger, issue, null);
    }

    private static List<ApplicationCommandProperties> BuildCommands(bool mediaForgeAvailable)
    {
        var commands = new List<ApplicationCommandProperties>();
        static SlashCommandBuilder Slash(string de, string en, string deDescription, string enDescription)
            => new SlashCommandBuilder().WithName(DiscordText.Command(de, en)).WithDescription(DiscordText.T(deDescription, enDescription));

        commands.Add(Slash("konto", "account", "Dein Jellyfin-Konto öffnen", "Open your Jellyfin account").Build());
        commands.Add(Slash("verbinden", "link", "Discord mit Jellyfin verbinden", "Link Discord to Jellyfin")
            .AddOption(DiscordText.Command("code", "code"), ApplicationCommandOptionType.String, DiscordText.T("Einmaliger Jellyfin-Code", "One-time Jellyfin code"), isRequired: true, minLength: 8, maxLength: 16).Build());
        commands.Add(Slash("statistik", "stats", "Deine Jellyfin-Statistik", "Your Jellyfin statistics")
            .AddOption(PeriodOption()).Build());
        commands.Add(Slash("bestenliste", "leaderboard", "Öffentliche Jellyfin-Bestenliste", "Public Jellyfin leaderboard")
            .AddOption(new SlashCommandOptionBuilder().WithName(DiscordText.Command("kategorie", "category")).WithDescription(DiscordText.T("Wert der Bestenliste", "Leaderboard value")).WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice(DiscordText.T("Watchtime", "Watch time"), "watchtime").AddChoice(DiscordText.T("Filme", "Movies"), "movies").AddChoice(DiscordText.T("Serien", "Series"), "series").AddChoice(DiscordText.T("Episoden", "Episodes"), "episodes"))
            .AddOption(PeriodOption()).Build());
        commands.Add(Slash("erfolge", "achievements", "Deine freigeschalteten Erfolge", "Your unlocked achievements").Build());
        commands.Add(Slash("datenschutz", "privacy", "Deine Datenschutzeinstellungen", "Your privacy settings").Build());
        commands.Add(Slash("aktuelle-streams", "now-playing", "Aktive Jellyfin-Streams", "Active Jellyfin streams").Build());
        commands.Add(Slash("zufall", "random", "Zufällige Empfehlung aus Jellyfin", "Random recommendation from Jellyfin")
            .AddOption(new SlashCommandOptionBuilder().WithName(DiscordText.Command("typ", "type")).WithDescription(DiscordText.T("Film oder Serie", "Movie or series")).WithType(ApplicationCommandOptionType.String).WithRequired(false).AddChoice(DiscordText.T("Film", "Movie"), "movie").AddChoice(DiscordText.T("Serie", "Series"), "series"))
            .AddOption(DiscordText.Command("genre", "genre"), ApplicationCommandOptionType.String, DiscordText.T("Optionales Genre", "Optional genre"), isRequired: false, maxLength: 50)
            .AddOption(DiscordText.Command("ungesehen", "unseen"), ApplicationCommandOptionType.Boolean, DiscordText.T("Nur ungesehene Inhalte", "Only unwatched content"), isRequired: false)
            .AddOption(DiscordText.Command("max-minuten", "max-minutes"), ApplicationCommandOptionType.Integer, DiscordText.T("Maximale Laufzeit", "Maximum runtime"), isRequired: false, minValue: 1, maxValue: 600)
            .AddOption(DiscordText.Command("min-bewertung", "min-rating"), ApplicationCommandOptionType.Number, DiscordText.T("Mindestbewertung", "Minimum rating"), isRequired: false, minValue: 0, maxValue: 10).Build());
        commands.Add(Slash("jellyfin-zugang", "jellyfin-access", "Jellyfin-Zugang beantragen", "Request Jellyfin access")
            .AddOption(DiscordText.Command("name", "name"), ApplicationCommandOptionType.String, DiscordText.T("Gewünschter Benutzername", "Requested username"), isRequired: true, minLength: 2, maxLength: 32).Build());
        commands.Add(Slash("konto-entsperren", "unlock-account", "Dein Jellyfin-Konto entsperren", "Unlock your Jellyfin account").Build());
        commands.Add(Slash("status", "status", "Jellyfin-Status anzeigen", "Show Jellyfin status").Build());
        commands.Add(Slash("hilfe", "help", "Verfügbare Befehle anzeigen", "Show available commands").Build());
        commands.Add(Slash("admin-hilfe", "admin-help", "Jellix-Adminbefehle anzeigen", "Show Jellix admin commands").Build());
        commands.Add(Slash("sticky", "sticky", "Sticky-Nachricht verwalten", "Manage a sticky message")
            .AddOption(new SlashCommandOptionBuilder().WithName(DiscordText.Command("aktion", "action")).WithDescription(DiscordText.T("Aktion", "Action")).WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice(DiscordText.T("Entfernen", "Remove"), "remove").AddChoice("Status", "status").AddChoice(DiscordText.T("Aktualisieren", "Refresh"), "refresh")).Build());

        if (Plugin.Instance?.Configuration.MediaForgeEnabled == true && mediaForgeAvailable)
        {
            commands.Add(Slash("anfragen", "requests", "Deine MediaForge-Anfragen", "Your MediaForge requests").Build());
            commands.Add(Slash("film-anfrage", "request-movie", "Einen Film anfragen", "Request a movie")
                .AddOption(DiscordText.Command("suche", "query"), ApplicationCommandOptionType.String, DiscordText.T("Titel des Films", "Movie title"), isRequired: true, minLength: 2, maxLength: 120).Build());
            commands.Add(Slash("serien-anfrage", "request-series", "Eine Serie anfragen", "Request a series")
                .AddOption(DiscordText.Command("suche", "query"), ApplicationCommandOptionType.String, DiscordText.T("Titel der Serie", "Series title"), isRequired: true, minLength: 2, maxLength: 120).Build());
        }

        commands.Add(new MessageCommandBuilder().WithName(DiscordText.T("Als Sticky markieren", "Set as sticky")).Build());
        return commands;
    }

    private static SlashCommandOptionBuilder PeriodOption()
        => new SlashCommandOptionBuilder().WithName(DiscordText.Command("zeitraum", "period")).WithDescription(DiscordText.T("Auswertungszeitraum", "Statistics period")).WithType(ApplicationCommandOptionType.String).WithRequired(false)
            .AddChoice(DiscordText.T("Heute", "Today"), "today").AddChoice(DiscordText.T("Woche", "Week"), "week").AddChoice(DiscordText.T("Monat", "Month"), "month").AddChoice(DiscordText.T("Jahr", "Year"), "year").AddChoice(DiscordText.T("Gesamt", "All"), "all");

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!RequireConfiguredGuild(command)) return;
        if (!AllowInteraction(command, "slash", 30)) return;
        try
        {
            var name = command.CommandName;
            if (name == DiscordText.Command("konto", "account")) await HandleAccountAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("verbinden", "link")) await HandleLinkAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("statistik", "stats")) await HandleStatsAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("bestenliste", "leaderboard")) await HandleLeaderboardAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("erfolge", "achievements")) await HandleAchievementsAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("datenschutz", "privacy")) await HandlePrivacyAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("aktuelle-streams", "now-playing")) await HandleNowPlayingAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("zufall", "random")) await HandleRandomAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("jellyfin-zugang", "jellyfin-access")) await HandleAccessRequestAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("konto-entsperren", "unlock-account")) await HandleUnlockAsync(command).ConfigureAwait(false);
            else if (name == "status") await HandleStatusAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("hilfe", "help")) await HandleHelpAsync(command, admin: false).ConfigureAwait(false);
            else if (name == DiscordText.Command("admin-hilfe", "admin-help")) await HandleHelpAsync(command, admin: true).ConfigureAwait(false);
            else if (name == "sticky") await HandleStickyCommandAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("anfragen", "requests")) await HandleRequestsAsync(command).ConfigureAwait(false);
            else if (name == DiscordText.Command("film-anfrage", "request-movie")) await HandleRequestSearchAsync(command, "movie").ConfigureAwait(false);
            else if (name == DiscordText.Command("serien-anfrage", "request-series")) await HandleRequestSearchAsync(command, "series").ConfigureAwait(false);
            else await command.RespondAsync(DiscordText.T("Dieser Befehl ist nicht mehr gültig.", "This command is no longer valid."), ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "slash command failed", exception);
            await RespondErrorAsync(command, DiscordText.T("Die Aktion konnte nicht abgeschlossen werden.", "The action could not be completed.")).ConfigureAwait(false);
        }
    }

    private async Task HandleLinkAsync(SocketSlashCommand command)
    {
        if (!RequireStreamingRole(command)) return;
        var guildId = command.GuildId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var code = GetString(command, DiscordText.Command("code", "code"));
        try
        {
            var link = await _linkService.ConsumeCodeAsync(code, guildId, command.User.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            var user = _userManager.GetUserById(link.JellyfinUserId);
            await TryWriteAuditAsync("discord-user", link.DiscordUserId, "account-linked", "jellyfin-user", link.JellyfinUserId.ToString("N"), string.Empty).ConfigureAwait(false);
            await command.RespondAsync(DiscordText.T($"✅ Dein Discord-Konto wurde mit Jellyfin-Benutzer **{Escape(user?.Username ?? "Jellyfin")}** verbunden.", $"✅ Your Discord account was linked to Jellyfin user **{Escape(user?.Username ?? "Jellyfin")}**."), ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await command.RespondAsync(DiscordText.Error(exception.Message), ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
        }
    }

    private async Task HandleAccountAsync(SocketSlashCommand command)
    {
        if (!RequireStreamingRole(command)) return;
        var link = await RequireLinkAsync(command).ConfigureAwait(false);
        if (link is null) return;
        var user = _userManager.GetUserById(link.JellyfinUserId);
        var streamingRole = Plugin.Instance?.Configuration.StreamingRoleId;
        var streamingAllowed = string.IsNullOrWhiteSpace(streamingRole)
            || (ulong.TryParse(streamingRole, out var streamingRoleId) && command.User is SocketGuildUser guildUser && guildUser.Roles.Any(role => role.Id == streamingRoleId));
        var embed = JellixEmbeds.Create(
                "👤 " + DiscordText.T("Dein Jellyfin-Konto", "Your Jellyfin account"),
                DiscordText.T("Alles Wichtige zu deinem Konto an einem Ort.", "Everything important about your account in one place."),
                JellixEmbeds.Primary,
                DiscordText.T("Privates Konto-Menü", "Private account menu"))
            .AddField(DiscordText.T("Jellyfin-Benutzer", "Jellyfin user"), $"**{Escape(Limit(user?.Username ?? DiscordText.T("Unbekannt", "Unknown"), 180))}**", true)
            .AddField("Discord", $"**@{Escape(Limit(command.User.Username, 100))}**", true)
            .AddField(DiscordText.T("Verbindung", "Connection"), DiscordText.T("🟢 Verbunden", "🟢 Linked"), true)
            .AddField("Streaming", streamingAllowed ? DiscordText.T("🟢 Aktiv", "🟢 Active") : DiscordText.T("🔴 Rolle fehlt", "🔴 Role missing"), true)
            .Build();
        var components = new ComponentBuilder()
            .WithButton(DiscordText.T("🔑 Passwort ändern", "🔑 Change password"), "account:password", disabled: Plugin.Instance?.Configuration.PasswordChangeEnabled != true || !PasswordTicketService.IsPublicUrlConfigured())
            .WithButton(DiscordText.T("📊 Statistiken", "📊 Statistics"), "account:stats", disabled: Plugin.Instance?.Configuration.StatisticsEnabled != true)
            .WithButton(DiscordText.T("📺 Weiterschauen", "📺 Continue watching"), "account:continue")
            .WithButton(DiscordText.T("📋 Anfragen", "📋 Requests"), "account:requests", disabled: Plugin.Instance?.Configuration.MediaForgeEnabled != true || !_mediaForge.IsAvailable)
            .WithButton(DiscordText.T("🏆 Erfolge", "🏆 Achievements"), "account:achievements", disabled: Plugin.Instance?.Configuration.AchievementsEnabled != true, row: 1)
            .WithButton(DiscordText.T("🔒 Datenschutz", "🔒 Privacy"), "account:privacy", row: 1)
            .WithButton(DiscordText.T("🔗 Verbindung lösen", "🔗 Unlink"), "account:unlink", ButtonStyle.Danger, row: 1)
            .Build();
        await command.RespondAsync(embed: embed, components: components, ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private async Task HandleStatsAsync(SocketSlashCommand command)
    {
        if (Plugin.Instance?.Configuration.StatisticsEnabled != true)
        {
            await command.RespondAsync(DiscordText.T("Statistiken sind deaktiviert.", "Statistics are disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!RequireStreamingRole(command)) return;
        var link = await RequireLinkAsync(command).ConfigureAwait(false);
        if (link is null) return;
        var period = GetString(command, DiscordText.Command("zeitraum", "period"), "all");
        await command.DeferAsync().ConfigureAwait(false);
        await SendStatsAsync(command, link, period, editOriginal: true).ConfigureAwait(false);
    }

    private async Task SendStatsAsync(IDiscordInteraction interaction, UserLink link, string period, bool editOriginal)
    {
        var summary = await _database.GetStatisticsAsync(link.JellyfinUserId, PeriodStartUtc(period), CancellationToken.None).ConfigureAwait(false);
        var user = _userManager.GetUserById(link.JellyfinUserId);
        var description = new StringBuilder()
            .AppendLine($"🎥 {DiscordText.T("Filme angesehen", "Movies watched")}: **{summary.MovieCount:N0}**")
            .AppendLine($"📺 {DiscordText.T("Serien", "Series")}: **{summary.SeriesCount:N0}**")
            .AppendLine($"🎞️ {DiscordText.T("Episoden", "Episodes")}: **{summary.EpisodeCount:N0}**")
            .AppendLine($"⏱️ Watchtime: **{FormatDuration(summary.WatchSeconds)}**");
        if (!string.IsNullOrWhiteSpace(summary.CurrentSeries)) description.AppendLine($"🔥 {DiscordText.T("Aktuelle Serie", "Current series")}: **{Escape(Limit(summary.CurrentSeries, 200))}**");
        if (!string.IsNullOrWhiteSpace(summary.TopSeries)) description.AppendLine($"⭐ {DiscordText.T("Meistgesehene Serie", "Most watched series")}: **{Escape(Limit(summary.TopSeries, 200))}**");
        var embed = JellixEmbeds.Create(
                Limit($"📊 {Escape(user?.Username ?? "Jellix")} – Jellyfin Stats", 256),
                Limit(description.ToString(), 4096),
                JellixEmbeds.Primary,
                DiscordText.T("Tatsächliche Wiedergabezeit • Erfassung seit der Jellix-Installation", "Actual playback time • Recorded since Jellix was installed"))
            .AddField(DiscordText.T("Zeitraum", "Period"), PeriodLabel(period), true)
            .Build();
        if (editOriginal)
        {
            await interaction.ModifyOriginalResponseAsync(properties => properties.Embed = embed).ConfigureAwait(false);
        }
        else
        {
            await interaction.FollowupAsync(embed: embed, ephemeral: false, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
        }
    }

    private async Task HandleLeaderboardAsync(SocketSlashCommand command)
    {
        if (Plugin.Instance?.Configuration.LeaderboardEnabled != true)
        {
            await command.RespondAsync(DiscordText.T("Die Bestenliste ist deaktiviert.", "The leaderboard is disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!RequireStreamingRole(command)) return;
        var category = GetString(command, DiscordText.Command("kategorie", "category"), "watchtime");
        var period = GetString(command, DiscordText.Command("zeitraum", "period"), "month");
        await command.DeferAsync().ConfigureAwait(false);
        var entries = await _database.GetLeaderboardAsync(category, PeriodStartUtc(period), 20, CancellationToken.None).ConfigureAwait(false);
        var lines = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var privacy = await _database.GetPrivacyAsync(entry.JellyfinUserId, CancellationToken.None).ConfigureAwait(false);
            var user = _userManager.GetUserById(entry.JellyfinUserId);
            var name = privacy.ShowNamePublicly ? Escape(Limit(user?.Username ?? "Unknown", 100)) : DiscordText.T("Privater Benutzer", "Private user");
            var prefix = i switch { 0 => "🥇", 1 => "🥈", 2 => "🥉", _ => $"{i + 1}." };
            var value = category == "watchtime" ? FormatDuration(entry.Value) : entry.Value.ToString("N0", CultureInfo.CurrentCulture);
            lines.Add($"{prefix} {name} — **{value}**");
        }

        var embed = JellixEmbeds.Create(
                "🏆 Jellyfin " + DiscordText.T("Bestenliste", "Leaderboard"),
                lines.Count == 0 ? DiscordText.T("Noch keine öffentlichen Daten.", "No public data yet.") : string.Join('\n', lines),
                JellixEmbeds.Gold,
                DiscordText.T("Nur Benutzer mit aktivierter Teilnahme werden angezeigt", "Only users who opted in are shown"))
            .AddField(DiscordText.T("Kategorie", "Category"), LeaderboardCategoryLabel(category), true)
            .AddField(DiscordText.T("Zeitraum", "Period"), PeriodLabel(period), true)
            .Build();
        await command.ModifyOriginalResponseAsync(properties => properties.Embed = embed).ConfigureAwait(false);
    }

    private async Task HandleAchievementsAsync(SocketSlashCommand command)
    {
        if (Plugin.Instance?.Configuration.AchievementsEnabled != true)
        {
            await command.RespondAsync(DiscordText.T("Achievements sind deaktiviert.", "Achievements are disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!RequireStreamingRole(command)) return;
        var link = await RequireLinkAsync(command).ConfigureAwait(false);
        if (link is null) return;
        var ids = await _database.ListAchievementsAsync(link.JellyfinUserId, CancellationToken.None).ConfigureAwait(false);
        var names = ids.Select(AchievementName).ToArray();
        var embed = JellixEmbeds.Create(
                "🏆 " + DiscordText.T("Deine Erfolge", "Your achievements"),
                names.Length == 0 ? DiscordText.T("Noch keine Erfolge freigeschaltet. Deine nächste Watch-Session zählt!", "No achievements unlocked yet. Your next watch session counts!") : string.Join('\n', names),
                JellixEmbeds.Gold,
                DiscordText.T($"{names.Length} Erfolge freigeschaltet", $"{names.Length} achievements unlocked"))
            .Build();
        await command.RespondAsync(embed: embed).ConfigureAwait(false);
    }

    private async Task HandlePrivacyAsync(SocketSlashCommand command)
    {
        if (!RequireStreamingRole(command)) return;
        var link = await RequireLinkAsync(command).ConfigureAwait(false);
        if (link is null) return;
        await SendPrivacyAsync(command, link).ConfigureAwait(false);
    }

    private async Task SendPrivacyAsync(IDiscordInteraction interaction, UserLink link, bool update = false)
    {
        var value = await _database.GetPrivacyAsync(link.JellyfinUserId, CancellationToken.None).ConfigureAwait(false);
        var embed = JellixEmbeds.Create("🔒 " + DiscordText.T("Deine Datenschutzeinstellungen", "Your privacy settings"), color: JellixEmbeds.Private, footer: DiscordText.T("Nur für dich sichtbar", "Only visible to you"))
            .WithDescription($"{Check(value.ShowInLeaderboard)} {DiscordText.T("Im Leaderboard erscheinen", "Appear in leaderboard")}\n{Check(value.ShowNamePublicly)} {DiscordText.T("Namen öffentlich anzeigen", "Show name publicly")}\n{Check(value.ShowNowPlaying)} Now Playing\n{Check(value.AnnounceAchievements)} {DiscordText.T("Achievement-Meldungen", "Achievement announcements")}")
            .Build();
        var components = new ComponentBuilder()
            .WithButton(DiscordText.T("Leaderboard", "Leaderboard"), "privacy:leaderboard")
            .WithButton(DiscordText.T("Name", "Name"), "privacy:name")
            .WithButton("Now Playing", "privacy:playing")
            .WithButton(DiscordText.T("Erfolge", "Achievements"), "privacy:achievements")
            .Build();
        if (update && interaction is SocketMessageComponent component)
        {
            await component.UpdateAsync(properties => { properties.Embed = embed; properties.Components = components; }).ConfigureAwait(false);
        }
        else
        {
            await interaction.RespondAsync(embed: embed, components: components, ephemeral: true).ConfigureAwait(false);
        }
    }

    private async Task HandleNowPlayingAsync(SocketSlashCommand command)
    {
        var mode = Plugin.Instance?.Configuration.NowPlayingMode ?? "admin";
        if (mode is "off" or "disabled")
        {
            await command.RespondAsync(DiscordText.T("Now Playing ist deaktiviert.", "Now Playing is disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (mode == "public" && !RequireStreamingRole(command)) return;
        if (mode == "admin" && !RequireAdmin(command)) return;
        var lines = new List<string>();
        foreach (var session in _sessionManager.Sessions.Where(value => value.NowPlayingItem is not null).Take(20))
        {
            UserPrivacy? privacy = null;
            if (mode == "public")
            {
                privacy = await _database.GetPrivacyAsync(session.UserId, CancellationToken.None).ConfigureAwait(false);
                if (!privacy.ShowNowPlaying) continue;
            }

            var item = session.NowPlayingItem;
            var title = item.Type == BaseItemKind.Episode
                ? $"📺 {Escape(Limit(item.SeriesName ?? item.Name, 160))} S{item.ParentIndexNumber:00}E{item.IndexNumber:00}"
                : $"🎬 {Escape(Limit(item.Name, 180))}";
            var mayShowName = Plugin.Instance?.Configuration.NowPlayingShowUsernames == true && (mode != "public" || privacy?.ShowNamePublicly == true);
            var user = mayShowName ? "**" + Escape(Limit(session.UserName, 100)) + "**\n" : string.Empty;
            lines.Add(user + title + $"\n{FormatPosition(session.PlayState.PositionTicks, item.RunTimeTicks)}");
        }

        var embed = JellixEmbeds.Create(
                $"🟢 {lines.Count} {DiscordText.T("Streams aktiv", "active streams")}",
                lines.Count == 0 ? DiscordText.T("Derzeit läuft nichts – Zeit für den nächsten Filmabend.", "Nothing is playing right now — time for the next movie night.") : JoinWithinLimit(lines, "\n\n", 4096),
                JellixEmbeds.Success,
                DiscordText.T("Live-Übersicht aus Jellyfin", "Live view from Jellyfin"))
            .Build();
        await command.RespondAsync(embed: embed, ephemeral: mode == "admin", allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private async Task HandleRandomAsync(SocketSlashCommand command)
    {
        if (Plugin.Instance?.Configuration.RandomEnabled != true)
        {
            await command.RespondAsync(DiscordText.T("Empfehlungen sind deaktiviert.", "Recommendations are disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!RequireStreamingRole(command)) return;
        await command.DeferAsync().ConfigureAwait(false);
        var type = GetString(command, DiscordText.Command("typ", "type"), "movie");
        var genre = GetString(command, "genre", string.Empty);
        var unseen = GetBoolean(command, DiscordText.Command("ungesehen", "unseen"));
        var maxMinutes = GetLong(command, DiscordText.Command("max-minuten", "max-minutes"));
        var minRating = GetDouble(command, DiscordText.Command("min-bewertung", "min-rating"));
        Jellyfin.Database.Implementations.Entities.User? user = null;
        if (unseen)
        {
            var guildId = command.GuildId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            var link = await _database.FindLinkByDiscordAsync(guildId, command.User.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            if (link is null)
            {
                await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.T("Für ungesehene Empfehlungen musst du dein Konto zuerst verbinden.", "Link your account before requesting unwatched recommendations.")).ConfigureAwait(false);
                return;
            }

            user = _userManager.GetUserById(link.JellyfinUserId);
            if (user is null)
            {
                await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.T("Der verknüpfte Jellyfin-Benutzer wurde nicht gefunden.", "The linked Jellyfin user was not found.")).ConfigureAwait(false);
                return;
            }
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IsVirtualItem = false,
            IncludeItemTypes = [type == "series" ? BaseItemKind.Series : BaseItemKind.Movie],
            IsPlayed = unseen && user is not null ? false : null,
        };
        var items = _libraryManager.GetItemList(query).Where(item =>
            (string.IsNullOrWhiteSpace(genre) || item.Genres.Any(value => string.Equals(value, genre, StringComparison.OrdinalIgnoreCase)))
            && (!maxMinutes.HasValue || item.RunTimeTicks.GetValueOrDefault() <= TimeSpan.FromMinutes(maxMinutes.Value).Ticks)
            && (!minRating.HasValue || item.CommunityRating.GetValueOrDefault() >= minRating.Value)).ToList();
        if (items.Count == 0)
        {
            await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.T("Keine passenden Inhalte gefunden.", "No matching content found.")).ConfigureAwait(false);
            return;
        }

        var selected = items[RandomNumberGenerator.GetInt32(items.Count)];
        var description = new StringBuilder();
        if (selected.CommunityRating.HasValue) description.AppendLine($"⭐ {selected.CommunityRating.Value:0.0}/10");
        if (selected.RunTimeTicks.HasValue) description.AppendLine($"⏱️ {TimeSpan.FromTicks(selected.RunTimeTicks.Value).TotalMinutes:0} min");
        if (selected.ProductionYear.HasValue) description.AppendLine($"📅 {selected.ProductionYear}");
        if (!string.IsNullOrWhiteSpace(selected.Overview)) description.AppendLine().Append(Escape(selected.Overview.Length > 700 ? selected.Overview[..700] + "…" : selected.Overview));
        var embed = JellixEmbeds.Create(
                Limit("🎲 " + Escape(selected.Name), 256),
                Limit(description.ToString(), 4096),
                JellixEmbeds.Secondary,
                DiscordText.T("Zufällig aus deiner Jellyfin-Bibliothek gewählt", "Randomly selected from your Jellyfin library"))
            .Build();
        await command.ModifyOriginalResponseAsync(properties => properties.Embed = embed).ConfigureAwait(false);
    }

    private async Task HandleUnlockAsync(SocketSlashCommand command)
    {
        if (Plugin.Instance?.Configuration.UnlockAccountEnabled != true)
        {
            await command.RespondAsync(DiscordText.T("Das Entsperren ist deaktiviert.", "Account unlock is disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!RequireStreamingRole(command)) return;
        var link = await RequireLinkAsync(command).ConfigureAwait(false);
        if (link is null) return;
        var user = _userManager.GetUserById(link.JellyfinUserId);
        if (user is null)
        {
            await command.RespondAsync(DiscordText.T("Jellyfin-Benutzer nicht gefunden.", "Jellyfin user not found."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        user.InvalidLoginAttemptCount = 0;
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        await TryWriteAuditAsync("discord-user", command.User.Id.ToString(CultureInfo.InvariantCulture), "account-unlocked", "jellyfin-user", user.Id.ToString("N"), string.Empty).ConfigureAwait(false);
        await command.RespondAsync("🔓 " + DiscordText.T("Dein Jellyfin-Konto wurde entsperrt.", "Your Jellyfin account was unlocked."), ephemeral: true).ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(SocketSlashCommand command)
    {
        var movieCount = _libraryManager.GetCount(new InternalItemsQuery { Recursive = true, IncludeItemTypes = [BaseItemKind.Movie] });
        var seriesCount = _libraryManager.GetCount(new InternalItemsQuery { Recursive = true, IncludeItemTypes = [BaseItemKind.Series] });
        var active = _sessionManager.Sessions.Count(value => value.NowPlayingItem is not null);
        var description = new StringBuilder().AppendLine("🟢 Jellyfin Online").AppendLine($"👥 {active} Streams").AppendLine($"🎬 {movieCount:N0} {DiscordText.T("Filme", "movies")}").AppendLine($"📺 {seriesCount:N0} {DiscordText.T("Serien", "series")}");
        var admin = IsAdmin(command.User);
        if (admin)
        {
            using var process = Process.GetCurrentProcess();
            description.AppendLine().AppendLine($"Version: {_applicationHost.ApplicationVersion}")
                .AppendLine($"Uptime: {FormatDuration((long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds)}")
                .AppendLine($"CPU: {SampleCpuPercentage(process):0.0}%")
                .AppendLine($"RAM: {process.WorkingSet64 / 1024 / 1024:N0} MB")
                .AppendLine($"Transcodes: {_sessionManager.Sessions.Count(value => value.TranscodingInfo is not null)}")
                .AppendLine($"Discord: {(_client.ConnectionState == ConnectionState.Connected ? "OK" : "Offline")}")
                .AppendLine($"MediaForge: {(_mediaForge.IsAvailable ? "OK" : DiscordText.T("Nicht verfügbar", "Unavailable"))}")
                .AppendLine(DiscordText.T("Datenbank: OK", "Database: OK"))
                .AppendLine($"Queue: {await _database.GetPendingNotificationCountAsync(CancellationToken.None).ConfigureAwait(false)}");
        }

        var statusEmbed = JellixEmbeds.Create(
                admin ? "🛠️ Jellix Admin Status" : "🟢 Jellyfin Status",
                description.ToString(),
                JellixEmbeds.Success,
                admin ? DiscordText.T("Interne Details • nur für Administratoren", "Internal details • administrators only") : DiscordText.T("Öffentlicher Serverstatus", "Public server status"))
            .Build();
        await command.RespondAsync(embed: statusEmbed, ephemeral: admin).ConfigureAwait(false);
    }

    private async Task HandleHelpAsync(SocketSlashCommand command, bool admin)
    {
        if (admin && !RequireAdmin(command)) return;
        var config = Plugin.Instance?.Configuration;
        var embedBuilder = JellixEmbeds.Create(
            admin ? "🛠️ Jellix Admin Command Center" : "✨ Jellix Command Guide",
            admin
                ? DiscordText.T("Werkzeuge für Administration, Freigaben und Stickies.", "Tools for administration, approvals, and stickies.")
                : DiscordText.T("Dein direkter Weg zu Jellyfin – einfach einen Befehl auswählen.", "Your direct path to Jellyfin — simply choose a command."),
            admin ? JellixEmbeds.Warning : JellixEmbeds.Primary,
            admin ? DiscordText.T("Nur für Administratoren", "Administrators only") : DiscordText.T("Normale Antworten bleiben im Kanal sichtbar", "Normal responses remain visible in the channel"));

        if (admin)
        {
            embedBuilder
                .AddField("📌 Sticky", DiscordText.T("`/sticky status` · `/sticky refresh` · `/sticky remove`\n**Erstellen:** Nachricht rechtsklicken → **Apps** → **Als Sticky markieren**", "`/sticky status` · `/sticky refresh` · `/sticky remove`\n**Create:** Right-click a message → **Apps** → **Set as sticky**"))
                .AddField(DiscordText.T("👤 Zugangsanfragen", "👤 Access requests"), DiscordText.T("Der Discord-Server-Owner erhält eine DM mit **Annehmen** und **Ablehnen**. Beim Ablehnen kann ein Grund angegeben werden.", "The Discord server owner receives a DM with **Accept** and **Reject**. A rejection reason can be provided."))
                .AddField(DiscordText.T("⚙️ Verwaltung", "⚙️ Administration"), DiscordText.T("Bot, Rollen, Kanäle, Warnungen und Funktionen werden im Jellyfin-Dashboard konfiguriert.", "Configure the bot, roles, channels, alerts, and features in the Jellyfin dashboard."));
        }
        else
        {
            embedBuilder
                .AddField(DiscordText.T("👤 Konto", "👤 Account"), DiscordText.T("`/konto` – privates Konto-Menü\n`/verbinden code` – Discord verknüpfen\n`/datenschutz` – Privatsphäre ändern\n`/konto-entsperren` – eigenes Konto entsperren", "`/account` – private account menu\n`/link code` – link Discord\n`/privacy` – change privacy\n`/unlock-account` – unlock your own account"), false)
                .AddField(DiscordText.T("📊 Community & Wiedergabe", "📊 Community & playback"), DiscordText.T("`/statistik zeitraum` – persönliche Statistik\n`/bestenliste kategorie zeitraum` – öffentliche Rangliste\n`/erfolge` – freigeschaltete Erfolge\n`/aktuelle-streams` – aktive Streams", "`/stats period` – personal statistics\n`/leaderboard category period` – public ranking\n`/achievements` – unlocked achievements\n`/now-playing` – active streams"), false)
                .AddField(DiscordText.T("🎲 Entdecken", "🎲 Discover"), DiscordText.T("`/zufall` – Empfehlung mit optionalen Filtern\n`/status` – Jellyfin-Serverstatus", "`/random` – recommendation with optional filters\n`/status` – Jellyfin server status"), true);
            if (config?.AccessRequestsEnabled == true)
            {
                embedBuilder.AddField(DiscordText.T("🚪 Zugang", "🚪 Access"), DiscordText.T("`/jellyfin-zugang name` – Zugang beantragen", "`/jellyfin-access name` – request access"), true);
            }
            if (config?.MediaForgeEnabled == true && _mediaForge.IsAvailable)
            {
                embedBuilder.AddField("🎬 MediaForge", DiscordText.T("`/film-anfrage suche`\n`/serien-anfrage suche`\n`/anfragen`", "`/request-movie query`\n`/request-series query`\n`/requests`"), true);
            }
        }

        var embed = embedBuilder.Build();
        await command.RespondAsync(embed: embed, ephemeral: admin, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private async Task HandleAccessRequestAsync(SocketSlashCommand command)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.AccessRequestsEnabled != true
            || !config.PasswordChangeEnabled
            || !PasswordTicketService.IsPublicUrlConfigured(config.JellyfinPublicUrl))
        {
            await command.RespondAsync(DiscordText.T("Zugangsanfragen sind deaktiviert.", "Access requests are disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        var guildId = command.GuildId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var discordId = command.User.Id.ToString(CultureInfo.InvariantCulture);
        if (await _database.FindLinkByDiscordAsync(guildId, discordId, CancellationToken.None).ConfigureAwait(false) is not null)
        {
            await command.RespondAsync(DiscordText.T("Dein Discord-Konto ist bereits mit Jellyfin verbunden.", "Your Discord account is already linked to Jellyfin."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        var last = await _database.GetLastAccessDecisionUtcAsync(guildId, discordId, CancellationToken.None).ConfigureAwait(false);
        if (last.HasValue && DateTime.UtcNow - last.Value < TimeSpan.FromHours(Math.Clamp(config.AccessRequestCooldownHours, 1, 24 * 365)))
        {
            await command.RespondAsync(DiscordText.T("Du kannst derzeit noch keine neue Anfrage stellen.", "You cannot submit another request yet."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        var requestedName = GetString(command, DiscordText.Command("name", "name")).Trim();
        if (requestedName.Length is < 2 or > 32
            || requestedName is "." or ".."
            || requestedName.Any(char.IsControl)
            || requestedName.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            await command.RespondAsync(DiscordText.T("Der Benutzername ist ungültig.", "The username is invalid."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (_userManager.GetUserByName(requestedName) is not null)
        {
            await command.RespondAsync(DiscordText.T("Dieser Jellyfin-Benutzername existiert bereits.", "This Jellyfin username already exists."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        if (!ulong.TryParse(config.GuildId, out var configuredGuildId) || _client.GetGuild(configuredGuildId) is not { } guild)
        {
            await command.RespondAsync(DiscordText.T("Der konfigurierte Discord-Server ist nicht erreichbar.", "The configured Discord server is unavailable."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        long id = 0;
        try
        {
            id = await _database.CreateAccessRequestAsync(guildId, discordId, requestedName, CancellationToken.None).ConfigureAwait(false);
            var embed = JellixEmbeds.Create(
                    "👤 " + DiscordText.T("Neue Jellyfin-Zugangsanfrage", "New Jellyfin access request"),
                    DiscordText.T("Bitte prüfe diese Anfrage und entscheide direkt über die Schaltflächen.", "Review this request and decide using the buttons below."),
                    JellixEmbeds.Warning,
                    DiscordText.T($"Anfrage #{id} • nur der Discord-Server-Owner kann entscheiden", $"Request #{id} • only the Discord server owner can decide"))
                .AddField("Discord", $"{command.User.Mention}\n`{command.User.Id}`", true)
                .AddField(DiscordText.T("Gewünschter Benutzername", "Requested username"), $"**{Escape(requestedName)}**", true)
                .AddField(DiscordText.T("Nach Annahme", "After approval"), DiscordText.T("Jellyfin-Konto + Verknüpfung + optional Streaming-Rolle", "Jellyfin account + link + optional streaming role"), false)
                .Build();
            var components = new ComponentBuilder().WithButton(DiscordText.T("Akzeptieren", "Accept"), $"access:approve:{id}", ButtonStyle.Success).WithButton(DiscordText.T("Ablehnen", "Reject"), $"access:reject:{id}", ButtonStyle.Danger).Build();
            var delivered = await TrySendDmAsync(guild.OwnerId, embed, components).ConfigureAwait(false);
            if (!delivered && ulong.TryParse(config.AccessRequestChannelId, out var fallbackChannelId) && _client.GetChannel(fallbackChannelId) is IMessageChannel fallbackChannel)
            {
                await fallbackChannel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
                delivered = true;
            }
            if (!delivered) throw new InvalidOperationException(DiscordText.T("Die Anfrage konnte dem Server-Owner nicht zugestellt werden.", "The request could not be delivered to the server owner."));
            await TryWriteAuditAsync("discord-user", discordId, "access-requested", "access-request", id.ToString(CultureInfo.InvariantCulture), requestedName).ConfigureAwait(false);
            await command.RespondAsync(DiscordText.T("✅ Deine Anfrage wurde gesendet.", "✅ Your request was submitted."), ephemeral: true).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            await command.RespondAsync(DiscordText.T("Du hast bereits eine offene Zugangsanfrage.", "You already have an open access request."), ephemeral: true).ConfigureAwait(false);
        }
        catch
        {
            if (id != 0) await _database.CancelPendingAccessRequestAsync(id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandleRequestsAsync(SocketSlashCommand command)
    {
        if (!RequireRequestAccess(command)) return;
        var link = await RequireLinkAsync(command).ConfigureAwait(false);
        if (link is null) return;
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        try
        {
            var user = _userManager.GetUserById(link.JellyfinUserId);
            using var response = await _mediaForge.InvokeAsync("list", link.JellyfinUserId, user?.Username ?? "unknown", null, CancellationToken.None).ConfigureAwait(false);
            var items = response.RootElement.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Take(20).ToArray() : [];
            var lines = items.Select(FormatRequest).ToArray();
            var embed = BuildRequestsEmbed(lines);
            await command.ModifyOriginalResponseAsync(properties => properties.Embed = embed).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaForgeBridgeException or MediaForgeBridgeUnavailableException)
        {
            await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.Error(exception.Message)).ConfigureAwait(false);
        }
    }

    private async Task HandleRequestSearchAsync(SocketSlashCommand command, string mediaType)
    {
        if (!RequireRequestAccess(command)) return;
        var link = await RequireLinkAsync(command).ConfigureAwait(false);
        if (link is null) return;
        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var query = GetString(command, DiscordText.Command("suche", "query"));
        try
        {
            var user = _userManager.GetUserById(link.JellyfinUserId);
            using var response = await _mediaForge.InvokeAsync("search", link.JellyfinUserId, user?.Username ?? "unknown", new { query, mediaType }, CancellationToken.None).ConfigureAwait(false);
            var items = response.RootElement.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Take(25).ToArray() : [];
            if (items.Length == 0)
            {
                await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.T("Keine Ergebnisse gefunden.", "No results found.")).ConfigureAwait(false);
                return;
            }

            var menu = new SelectMenuBuilder().WithCustomId("mediaforge:select").WithPlaceholder(DiscordText.T("Titel auswählen", "Select a title"));
            foreach (var item in items)
            {
                if (!item.TryGetProperty("token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("title", out var titleValue) || titleValue.ValueKind != JsonValueKind.String) continue;
                var token = tokenValue.GetString() ?? string.Empty;
                var title = titleValue.GetString() ?? "MediaForge";
                var year = item.TryGetProperty("year", out var yearValue) ? yearValue.ToString() : string.Empty;
                if (token.Length is > 0 and <= 100) menu.AddOption(Limit(title, 100), token, year.Length > 0 ? Limit(year, 100) : null);
            }

            if (menu.Options.Count == 0)
            {
                await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.T("MediaForge hat keine gültigen Ergebnisse geliefert.", "MediaForge returned no valid results.")).ConfigureAwait(false);
                return;
            }

            await command.ModifyOriginalResponseAsync(properties =>
            {
                properties.Content = DiscordText.T("Wähle den gewünschten Titel aus:", "Choose the requested title:");
                properties.Components = new ComponentBuilder().WithSelectMenu(menu).Build();
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaForgeBridgeException or MediaForgeBridgeUnavailableException)
        {
            await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.Error(exception.Message)).ConfigureAwait(false);
        }
    }

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        var isAccessDecision = component.Data.CustomId.StartsWith("access:", StringComparison.Ordinal);
        if (!isAccessDecision && !RequireConfiguredGuild(component)) return;
        if (!AllowInteraction(component, "button", 60)) return;
        try
        {
            var id = component.Data.CustomId;
            if (id.StartsWith("privacy:", StringComparison.Ordinal)) await HandlePrivacyButtonAsync(component, id[8..]).ConfigureAwait(false);
            else if (id.StartsWith("account:", StringComparison.Ordinal)) await HandleAccountButtonAsync(component, id[8..]).ConfigureAwait(false);
            else if (id.StartsWith("access:", StringComparison.Ordinal)) await HandleAccessDecisionAsync(component, id).ConfigureAwait(false);
            else await component.RespondAsync(DiscordText.T("Diese Schaltfläche ist nicht mehr gültig.", "This button is no longer valid."), ephemeral: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "button interaction failed", exception);
            await RespondErrorAsync(component, DiscordText.T("Die Aktion konnte nicht abgeschlossen werden.", "The action could not be completed.")).ConfigureAwait(false);
        }
    }

    private async Task HandleAccountButtonAsync(SocketMessageComponent component, string action)
    {
        if (!RequireStreamingRole(component)) return;
        var link = await RequireLinkAsync(component).ConfigureAwait(false);
        if (link is null) return;
        if (action == "password")
        {
            if (_userManager.GetUserById(link.JellyfinUserId) is null)
            {
                await component.RespondAsync(DiscordText.T("Der verknüpfte Jellyfin-Benutzer wurde nicht gefunden.", "The linked Jellyfin user was not found."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            if (!PasswordTicketService.IsPublicUrlConfigured())
            {
                await component.RespondAsync(DiscordText.T("Die öffentliche Jellyfin-URL ist noch nicht gültig konfiguriert.", "The public Jellyfin URL is not configured correctly."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            var ticket = await _passwordTickets.CreateAsync(link.JellyfinUserId, link.DiscordUserId, CancellationToken.None).ConfigureAwait(false);
            var url = PasswordTicketService.BuildUrl(ticket.Token);
            await component.RespondAsync(DiscordText.T($"Öffne diesen einmaligen Link, um dein Passwort zu ändern:\n{url}", $"Open this one-time link to change your password:\n{url}"), ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
        }
        else if (action == "stats")
        {
            if (Plugin.Instance?.Configuration.StatisticsEnabled != true)
            {
                await component.RespondAsync(DiscordText.T("Statistiken sind deaktiviert.", "Statistics are disabled."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
            await SendStatsAsync(component, link, "all", editOriginal: true).ConfigureAwait(false);
        }
        else if (action == "continue")
        {
            await SendContinueWatchingAsync(component, link).ConfigureAwait(false);
        }
        else if (action == "requests")
        {
            await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
            try
            {
                var user = _userManager.GetUserById(link.JellyfinUserId);
                using var response = await _mediaForge.InvokeAsync("list", link.JellyfinUserId, user?.Username ?? "unknown", null, CancellationToken.None).ConfigureAwait(false);
                var items = response.RootElement.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Take(20).ToArray() : [];
                var lines = items.Select(FormatRequest).ToArray();
                var embed = BuildRequestsEmbed(lines);
                await component.ModifyOriginalResponseAsync(properties => properties.Embed = embed).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is MediaForgeBridgeException or MediaForgeBridgeUnavailableException)
            {
                await component.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.Error(exception.Message)).ConfigureAwait(false);
            }
        }
        else if (action == "achievements")
        {
            if (Plugin.Instance?.Configuration.AchievementsEnabled != true)
            {
                await component.RespondAsync(DiscordText.T("Achievements sind deaktiviert.", "Achievements are disabled."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            var ids = await _database.ListAchievementsAsync(link.JellyfinUserId, CancellationToken.None).ConfigureAwait(false);
            var achievementEmbed = JellixEmbeds.Create(
                    "🏆 " + DiscordText.T("Deine Erfolge", "Your achievements"),
                    ids.Count == 0 ? DiscordText.T("Noch keine Erfolge – bleib dran!", "No achievements yet — keep watching!") : string.Join('\n', ids.Select(AchievementName)),
                    JellixEmbeds.Gold,
                    DiscordText.T("Nur für dich sichtbar", "Only visible to you"))
                .Build();
            await component.RespondAsync(embed: achievementEmbed, ephemeral: true).ConfigureAwait(false);
        }
        else if (action == "privacy")
        {
            await SendPrivacyAsync(component, link).ConfigureAwait(false);
        }
        else if (action == "unlink")
        {
            var components = new ComponentBuilder().WithButton(DiscordText.T("Wirklich trennen", "Confirm unlink"), "account:unlink-confirm", ButtonStyle.Danger).Build();
            await component.RespondAsync(DiscordText.T("Dadurch funktionieren Account-, Statistik- und Requestfunktionen nicht mehr.", "Account, statistics and request features will stop working."), components: components, ephemeral: true).ConfigureAwait(false);
        }
        else if (action == "unlink-confirm")
        {
            await _database.UnlinkUserAsync(link.GuildId, link.DiscordUserId, CancellationToken.None).ConfigureAwait(false);
            await TryWriteAuditAsync("discord-user", link.DiscordUserId, "account-unlinked", "jellyfin-user", link.JellyfinUserId.ToString("N"), string.Empty).ConfigureAwait(false);
            await component.UpdateAsync(properties => { properties.Content = DiscordText.T("Die Verbindung wurde getrennt.", "The account was unlinked."); properties.Embed = null; properties.Components = new ComponentBuilder().Build(); }).ConfigureAwait(false);
        }
        else
        {
            await component.RespondAsync(DiscordText.T("Diese Kontoaktion ist nicht mehr gültig.", "This account action is no longer valid."), ephemeral: true).ConfigureAwait(false);
        }
    }

    private async Task SendContinueWatchingAsync(SocketMessageComponent component, UserLink link)
    {
        var user = _userManager.GetUserById(link.JellyfinUserId);
        if (user is null)
        {
            await component.RespondAsync(DiscordText.T("Jellyfin-Benutzer nicht gefunden.", "Jellyfin user not found."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            Recursive = true,
            IsResumable = true,
            IsVirtualItem = false,
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Limit = 10,
        });
        var lines = items.Select(item => item is MediaBrowser.Controller.Entities.TV.Episode episode
            ? $"📺 **{Escape(Limit(episode.SeriesName, 180))}** – S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
            : $"🎬 **{Escape(Limit(item.Name, 180))}**").ToArray();
        var embed = JellixEmbeds.Create(
                "📺 " + DiscordText.T("Weiterschauen", "Continue watching"),
                lines.Length == 0 ? DiscordText.T("Keine angefangenen Inhalte. Zeit, etwas Neues zu entdecken!", "Nothing to continue. Time to discover something new!") : JoinWithinLimit(lines, "\n", 4096),
                JellixEmbeds.Primary,
                DiscordText.T("Nur für dich sichtbar", "Only visible to you"))
            .Build();
        await component.RespondAsync(embed: embed, ephemeral: true).ConfigureAwait(false);
    }

    private async Task HandlePrivacyButtonAsync(SocketMessageComponent component, string field)
    {
        var link = await RequireLinkAsync(component).ConfigureAwait(false);
        if (link is null) return;
        var old = await _database.GetPrivacyAsync(link.JellyfinUserId, CancellationToken.None).ConfigureAwait(false);
        var updated = field switch
        {
            "leaderboard" => old with { ShowInLeaderboard = !old.ShowInLeaderboard },
            "name" => old with { ShowNamePublicly = !old.ShowNamePublicly },
            "playing" => old with { ShowNowPlaying = !old.ShowNowPlaying },
            "achievements" => old with { AnnounceAchievements = !old.AnnounceAchievements },
            _ => old,
        };
        await _database.SetPrivacyAsync(updated, CancellationToken.None).ConfigureAwait(false);
        await SendPrivacyAsync(component, link, update: true).ConfigureAwait(false);
    }

    private async Task HandleAccessDecisionAsync(SocketMessageComponent component, string customId)
    {
        var parts = customId.Split(':');
        if (parts.Length != 3 || parts[1] is not ("approve" or "reject") || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            await component.RespondAsync(DiscordText.T("Diese Aktion ist ungültig.", "This action is invalid."), ephemeral: true).ConfigureAwait(false);
            return;
        }
        var initialRequest = await _database.GetAccessRequestAsync(id, CancellationToken.None).ConfigureAwait(false);
        if (initialRequest is null || !IsAccessReviewer(component.User, initialRequest.GuildId))
        {
            await component.RespondAsync(DiscordText.T("Nur der Discord-Server-Owner darf diese Anfrage bearbeiten.", "Only the Discord server owner may process this request."), ephemeral: component.GuildId.HasValue).ConfigureAwait(false);
            return;
        }
        if (initialRequest.Status != "pending")
        {
            await component.RespondAsync(DiscordText.T("Diese Anfrage wurde bereits bearbeitet.", "This request was already processed."), ephemeral: component.GuildId.HasValue).ConfigureAwait(false);
            return;
        }
        if (parts[1] == "reject")
        {
            var modal = new ModalBuilder()
                .WithTitle(DiscordText.T("Zugangsanfrage ablehnen", "Reject access request"))
                .WithCustomId($"access-reject:{id}")
                .AddTextInput(
                    DiscordText.T("Grund (optional)", "Reason (optional)"),
                    "reason",
                    TextInputStyle.Paragraph,
                    DiscordText.T("Kurze Begründung für den Benutzer", "Short explanation for the user"),
                    maxLength: 500,
                    required: false)
                .Build();
            await component.RespondWithModalAsync(modal).ConfigureAwait(false);
            return;
        }
        await _accessDecisionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var request = await _database.GetAccessRequestAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (request is null || request.Status != "pending")
            {
                await component.RespondAsync(DiscordText.T("Diese Anfrage wurde bereits bearbeitet.", "This request was already processed."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            if (_userManager.GetUserByName(request.RequestedName) is not null)
            {
                await component.RespondAsync(DiscordText.T("Dieser Jellyfin-Benutzername existiert bereits.", "This Jellyfin username already exists."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            if (await _database.FindLinkByDiscordAsync(request.GuildId, request.DiscordUserId, CancellationToken.None).ConfigureAwait(false) is not null)
            {
                await component.RespondAsync(DiscordText.T("Dieses Discord-Konto wurde inzwischen verbunden.", "This Discord account has since been linked."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            var config = Plugin.Instance?.Configuration;
            if (config?.PasswordChangeEnabled != true || !PasswordTicketService.IsPublicUrlConfigured(config.JellyfinPublicUrl))
            {
                await component.RespondAsync(DiscordText.T("Passwortänderung und öffentliche Jellyfin-URL müssen zuerst konfiguriert werden.", "Password changes and the public Jellyfin URL must be configured first."), ephemeral: true).ConfigureAwait(false);
                return;
            }

            await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
            Jellyfin.Database.Implementations.Entities.User? createdUser = null;
            var linkCreated = false;
            var approved = false;
            try
            {
                createdUser = await _userManager.CreateUserAsync(request.RequestedName).ConfigureAwait(false);
                createdUser.SetPermission(PermissionKind.IsAdministrator, false);
                await _userManager.UpdateUserAsync(createdUser).ConfigureAwait(false);
                var bootstrapPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await _userManager.ChangePassword(createdUser.Id, bootstrapPassword).ConfigureAwait(false);
                await _database.LinkUserAsync(request.GuildId, request.DiscordUserId, createdUser.Id, "access-approval", CancellationToken.None).ConfigureAwait(false);
                linkCreated = true;
                var ticket = await _passwordTickets.CreateAsync(createdUser.Id, request.DiscordUserId, CancellationToken.None).ConfigureAwait(false);
                var url = PasswordTicketService.BuildUrl(ticket.Token);
                approved = await _database.DecideAccessRequestAsync(id, "approved", component.User.Id.ToString(CultureInfo.InvariantCulture), null, CancellationToken.None).ConfigureAwait(false);
                if (!approved) throw new InvalidOperationException("The access request was already processed.");

                await TryAssignStreamingRoleAsync(request.GuildId, request.DiscordUserId).ConfigureAwait(false);
                var delivered = ulong.TryParse(request.DiscordUserId, NumberStyles.None, CultureInfo.InvariantCulture, out var discordUserId)
                    && await TrySendDmAsync(discordUserId, DiscordText.T($"Dein Jellyfin-Zugang wurde erstellt. Lege hier dein Passwort fest:\n{url}", $"Your Jellyfin access was created. Set your password here:\n{url}")).ConfigureAwait(false);
                await TryWriteAuditAsync("discord-admin", component.User.Id.ToString(CultureInfo.InvariantCulture), "access-approved", "jellyfin-user", createdUser.Id.ToString("N"), string.Empty).ConfigureAwait(false);
                var approvedDescription = delivered
                    ? DiscordText.T("Das Jellyfin-Konto wurde erstellt, mit Discord verbunden und der Benutzer wurde sicher benachrichtigt.", "The Jellyfin account was created, linked to Discord, and the user was notified securely.")
                    : DiscordText.T($"Das Konto wurde erstellt, aber die DM konnte nicht zugestellt werden. Gib dem Benutzer diesen einmaligen Link sicher weiter:\n{url}", $"The account was created, but the DM could not be delivered. Securely pass this one-time link to the user:\n{url}");
                var approvedEmbed = JellixEmbeds.Create(
                        "✅ " + DiscordText.T("Zugang freigegeben", "Access approved"),
                        approvedDescription,
                        JellixEmbeds.Success,
                        DiscordText.T($"Anfrage #{id} • {createdUser.Username}", $"Request #{id} • {createdUser.Username}"))
                    .Build();
                await component.ModifyOriginalResponseAsync(properties =>
                {
                    properties.Content = null;
                    properties.Embed = approvedEmbed;
                    properties.Components = new ComponentBuilder().Build();
                }).ConfigureAwait(false);
            }
            catch
            {
                if (!approved && createdUser is not null)
                {
                    if (linkCreated)
                    {
                        try { await _database.UnlinkUserAsync(request.GuildId, request.DiscordUserId, CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception exception) { LogDiscordWarning(_logger, "access approval link rollback failed", exception); }
                    }

                    try { await _userManager.DeleteUserAsync(createdUser.Id).ConfigureAwait(false); }
                    catch (Exception exception) { LogDiscordWarning(_logger, "access approval user rollback failed", exception); }
                }

                throw;
            }
        }
        finally
        {
            _accessDecisionLock.Release();
        }
    }

    private async Task OnModalSubmittedAsync(SocketModal modal)
    {
        if (!AllowInteraction(modal, "modal", 30)) return;
        if (!modal.Data.CustomId.StartsWith("access-reject:", StringComparison.Ordinal)
            || !long.TryParse(modal.Data.CustomId[14..], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            await modal.RespondAsync(DiscordText.T("Dieses Formular ist nicht mehr gültig.", "This form is no longer valid."), ephemeral: modal.GuildId.HasValue).ConfigureAwait(false);
            return;
        }

        var rejected = false;
        await _accessDecisionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var request = await _database.GetAccessRequestAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (request is null || !IsAccessReviewer(modal.User, request.GuildId))
            {
                await modal.RespondAsync(DiscordText.T("Du darfst diese Anfrage nicht bearbeiten.", "You may not process this request."), ephemeral: modal.GuildId.HasValue).ConfigureAwait(false);
                return;
            }
            if (request.Status != "pending")
            {
                await modal.RespondAsync(DiscordText.T("Diese Anfrage wurde bereits bearbeitet.", "This request was already processed."), ephemeral: modal.GuildId.HasValue).ConfigureAwait(false);
                return;
            }

            var reason = modal.Data.Components.FirstOrDefault(value => value.CustomId == "reason")?.Value?.Trim() ?? string.Empty;
            reason = Limit(reason, 500);
            if (!await _database.DecideAccessRequestAsync(id, "rejected", modal.User.Id.ToString(CultureInfo.InvariantCulture), reason, CancellationToken.None).ConfigureAwait(false))
            {
                await modal.RespondAsync(DiscordText.T("Diese Anfrage wurde bereits bearbeitet.", "This request was already processed."), ephemeral: modal.GuildId.HasValue).ConfigureAwait(false);
                return;
            }
            rejected = true;

            await TryWriteAuditAsync("discord-admin", modal.User.Id.ToString(CultureInfo.InvariantCulture), "access-rejected", "access-request", id.ToString(CultureInfo.InvariantCulture), string.Empty).ConfigureAwait(false);
            var rejectedText = DiscordText.T("Deine Jellyfin-Zugangsanfrage wurde abgelehnt.", "Your Jellyfin access request was rejected.");
            if (!string.IsNullOrWhiteSpace(reason)) rejectedText += DiscordText.T("\nGrund: ", "\nReason: ") + reason;
            if (ulong.TryParse(request.DiscordUserId, NumberStyles.None, CultureInfo.InvariantCulture, out var rejectedUserId))
            {
                _ = await TrySendDmAsync(rejectedUserId, rejectedText).ConfigureAwait(false);
            }

            var decisionEmbed = JellixEmbeds.Create(
                    "❌ " + DiscordText.T("Zugangsanfrage abgelehnt", "Access request rejected"),
                    string.IsNullOrWhiteSpace(reason) ? DiscordText.T("Es wurde kein Grund angegeben.", "No reason was provided.") : DiscordText.T("Grund: ", "Reason: ") + Escape(reason),
                    JellixEmbeds.Danger,
                    DiscordText.T($"Anfrage #{id} • bearbeitet von {modal.User.Username}", $"Request #{id} • processed by {modal.User.Username}"))
                .Build();
            await modal.UpdateAsync(properties => { properties.Content = null; properties.Embed = decisionEmbed; properties.Components = new ComponentBuilder().Build(); }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "access rejection failed", exception);
            await RespondErrorAsync(modal, rejected
                ? DiscordText.T("Die Ablehnung wurde gespeichert, aber die Bestätigung konnte nicht vollständig zugestellt werden.", "The rejection was saved, but its confirmation could not be delivered completely.")
                : DiscordText.T("Die Ablehnung konnte nicht gespeichert werden.", "The rejection could not be saved.")).ConfigureAwait(false);
        }
        finally
        {
            _accessDecisionLock.Release();
        }
    }

    private async Task OnSelectMenuAsync(SocketMessageComponent component)
    {
        if (!RequireConfiguredGuild(component)) return;
        if (!AllowInteraction(component, "select", 15)) return;
        if (component.Data.CustomId != "mediaforge:select" || component.Data.Values.Count == 0)
        {
            await component.RespondAsync(DiscordText.T("Diese Auswahl ist nicht mehr gültig.", "This selection is no longer valid."), ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!RequireRequestAccess(component)) return;
        var link = await RequireLinkAsync(component).ConfigureAwait(false);
        if (link is null) return;
        await component.DeferAsync(ephemeral: true).ConfigureAwait(false);
        try
        {
            var user = _userManager.GetUserById(link.JellyfinUserId);
            using var response = await _mediaForge.InvokeAsync("submit", link.JellyfinUserId, user?.Username ?? "unknown", new { selectionToken = component.Data.Values.First() }, CancellationToken.None).ConfigureAwait(false);
            var title = response.RootElement.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() : null;
            var status = response.RootElement.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString() : "pending";
            var requestId = response.RootElement.TryGetProperty("id", out var idValue) ? Limit(idValue.ToString(), 128) : "unknown";
            await TryWriteAuditAsync("discord-user", link.DiscordUserId, "request-submitted", "mediaforge-request", requestId, title ?? string.Empty).ConfigureAwait(false);
            await component.ModifyOriginalResponseAsync(properties => { properties.Content = $"✅ **{Escape(Limit(title ?? "MediaForge", 180))}** – {Escape(Limit(status ?? "pending", 64))}"; properties.Components = new ComponentBuilder().Build(); }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is MediaForgeBridgeException or MediaForgeBridgeUnavailableException)
        {
            await component.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.Error(exception.Message)).ConfigureAwait(false);
        }
    }

    private async Task OnMessageCommandAsync(SocketMessageCommand command)
    {
        if (!RequireConfiguredGuild(command)) return;
        if (!AllowInteraction(command, "message-command", 10)) return;
        if (!RequireAdmin(command)) return;
        if (Plugin.Instance?.Configuration.StickyEnabled != true)
        {
            await command.RespondAsync(DiscordText.T("Sticky-Nachrichten sind deaktiviert.", "Sticky messages are disabled."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        var message = command.Data.Message;
        var payload = StickyPayload.FromMessage(message);
        if (string.IsNullOrWhiteSpace(payload.Content) && payload.Embeds.Count == 0 && payload.AttachmentUrls.Count == 0)
        {
            await command.RespondAsync(DiscordText.T("Diese Nachricht hat keinen speicherbaren Inhalt.", "This message has no storable content."), ephemeral: true).ConfigureAwait(false);
            return;
        }

        await command.DeferAsync(ephemeral: true).ConfigureAwait(false);
        var guildId = command.GuildId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var channelId = message.Channel.Id.ToString(CultureInfo.InvariantCulture);
        var existing = await _database.GetStickyAsync(guildId, channelId, CancellationToken.None).ConfigureAwait(false);
        var posted = await message.Channel.SendMessageAsync(payload.BuildContent(), embeds: payload.BuildEmbeds(), allowedMentions: AllowedMentions.None).ConfigureAwait(false);
        var record = new StickyMessageRecord(guildId, channelId, message.Id.ToString(CultureInfo.InvariantCulture), posted.Id.ToString(CultureInfo.InvariantCulture), JsonSerializer.Serialize(payload), command.User.Id.ToString(CultureInfo.InvariantCulture), true, DateTime.UtcNow);
        try
        {
            await _database.UpsertStickyAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            try { await posted.DeleteAsync().ConfigureAwait(false); } catch (HttpException) { }
            throw;
        }

        await DeleteStickyMessageAsync(message.Channel, message.Id).ConfigureAwait(false);
        if (existing is not null && ulong.TryParse(existing.CurrentMessageId, out var previousId) && previousId != message.Id && previousId != posted.Id)
        {
            await DeleteStickyMessageAsync(message.Channel, previousId).ConfigureAwait(false);
        }

        await TryWriteAuditAsync("discord-admin", command.User.Id.ToString(CultureInfo.InvariantCulture), "sticky-created", "discord-channel", message.Channel.Id.ToString(CultureInfo.InvariantCulture), string.Empty).ConfigureAwait(false);
        await command.ModifyOriginalResponseAsync(properties => properties.Content = DiscordText.T("Sticky-Nachricht gespeichert.", "Sticky message saved.")).ConfigureAwait(false);
    }

    private async Task HandleStickyCommandAsync(SocketSlashCommand command)
    {
        if (!RequireAdmin(command)) return;
        var action = GetString(command, DiscordText.Command("aktion", "action"));
        var guild = command.GuildId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var channel = command.Channel.Id.ToString(CultureInfo.InvariantCulture);
        var sticky = await _database.GetStickyAsync(guild, channel, CancellationToken.None).ConfigureAwait(false);
        if (action == "status")
        {
            await command.RespondAsync(sticky?.Enabled == true ? DiscordText.T("Sticky ist aktiv.", "Sticky is active.") : DiscordText.T("In diesem Kanal ist kein Sticky aktiv.", "No sticky is active in this channel."), ephemeral: true).ConfigureAwait(false);
        }
        else if (action == "remove")
        {
            if (sticky is not null && ulong.TryParse(sticky.CurrentMessageId, out var messageId))
            {
                await DeleteStickyMessageAsync(command.Channel, messageId).ConfigureAwait(false);
            }

            await _database.DeleteStickyAsync(guild, channel, CancellationToken.None).ConfigureAwait(false);
            await TryWriteAuditAsync("discord-admin", command.User.Id.ToString(CultureInfo.InvariantCulture), "sticky-removed", "discord-channel", channel, string.Empty).ConfigureAwait(false);
            await command.RespondAsync(DiscordText.T("Sticky entfernt.", "Sticky removed."), ephemeral: true).ConfigureAwait(false);
        }
        else if (action == "refresh" && sticky is not null)
        {
            await RefreshStickyAsync(sticky, CancellationToken.None).ConfigureAwait(false);
            await command.RespondAsync(DiscordText.T("Sticky aktualisiert.", "Sticky refreshed."), ephemeral: true).ConfigureAwait(false);
        }
        else
        {
            await command.RespondAsync(DiscordText.T("Kein Sticky vorhanden.", "No sticky found."), ephemeral: true).ConfigureAwait(false);
        }
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        if (message.Author.Id == _client.CurrentUser?.Id
            || message.Channel is not SocketGuildChannel guildChannel
            || Plugin.Instance?.Configuration is not { StickyEnabled: true } config
            || !ulong.TryParse(config.GuildId, out var configuredGuildId)
            || guildChannel.Guild.Id != configuredGuildId)
        {
            return Task.CompletedTask;
        }

        ScheduleStickyRefresh(guildChannel.Guild.Id, message.Channel.Id);
        return Task.CompletedTask;
    }

    private async Task OnMessageDeletedAsync(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channel)
    {
        if (_expectedStickyDeletes.TryRemove(message.Id, out _)) return;
        var config = Plugin.Instance?.Configuration;
        if (config?.StickyEnabled != true || !ulong.TryParse(config.GuildId, out var guildId)) return;
        var sticky = await _database.GetStickyAsync(guildId.ToString(CultureInfo.InvariantCulture), channel.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        if (sticky?.Enabled == true && sticky.CurrentMessageId == message.Id.ToString(CultureInfo.InvariantCulture))
        {
            ScheduleStickyRefresh(guildId, channel.Id);
        }
    }

    private void ScheduleStickyRefresh(ulong guildId, ulong channelId)
    {
        CancellationTokenSource current;
        lock (_stickySync)
        {
            if (_stickyDebounce.Remove(channelId, out var old))
            {
                old.Cancel();
                old.Dispose();
            }

            current = new CancellationTokenSource();
            _stickyDebounce[channelId] = current;
        }

        _ = DebouncedRefreshAsync(guildId, channelId, current);
    }

    private async Task DebouncedRefreshAsync(ulong guildId, ulong channelId, CancellationTokenSource owner)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(Plugin.Instance?.Configuration.StickyDebounceSeconds ?? 2, 1, 10)), owner.Token).ConfigureAwait(false);
            var sticky = await _database.GetStickyAsync(guildId.ToString(CultureInfo.InvariantCulture), channelId.ToString(CultureInfo.InvariantCulture), owner.Token).ConfigureAwait(false);
            if (sticky?.Enabled == true) await RefreshStickyAsync(sticky, owner.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer channel message restarted the debounce window.
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "sticky refresh failed", exception);
        }
        finally
        {
            lock (_stickySync)
            {
                if (_stickyDebounce.TryGetValue(channelId, out var current) && ReferenceEquals(current, owner))
                {
                    _stickyDebounce.Remove(channelId);
                    owner.Dispose();
                }
            }
        }
    }

    private async Task RefreshStickyAsync(StickyMessageRecord sticky, CancellationToken cancellationToken)
    {
        if (!ulong.TryParse(sticky.ChannelId, out var channelId) || _client.GetChannel(channelId) is not IMessageChannel channel) return;
        var gate = _stickyRefreshLocks.GetOrAdd(channelId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var latest = await _database.GetStickyAsync(sticky.GuildId, sticky.ChannelId, cancellationToken).ConfigureAwait(false) ?? sticky;
            if (!latest.Enabled) return;
            var payload = JsonSerializer.Deserialize<StickyPayload>(latest.ContentJson) ?? new StickyPayload();
            var message = await channel.SendMessageAsync(payload.BuildContent(), embeds: payload.BuildEmbeds(), allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            try
            {
                await _database.UpsertStickyAsync(latest with { CurrentMessageId = message.Id.ToString(CultureInfo.InvariantCulture), LastRepostedUtc = DateTime.UtcNow }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DeleteStickyMessageAsync(channel, message.Id).ConfigureAwait(false);
                throw;
            }

            if (ulong.TryParse(latest.CurrentMessageId, out var oldId) && oldId != message.Id)
            {
                await DeleteStickyMessageAsync(channel, oldId).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DeleteStickyMessageAsync(IMessageChannel channel, ulong messageId)
    {
        _expectedStickyDeletes[messageId] = 0;
        if (_expectedStickyDeletes.Count > 1000) _expectedStickyDeletes.Clear();
        try
        {
            await channel.DeleteMessageAsync(messageId).ConfigureAwait(false);
        }
        catch (HttpException)
        {
            _expectedStickyDeletes.TryRemove(messageId, out _);
        }
    }

    private async Task RestoreStickiesAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.StickyEnabled != true || !ulong.TryParse(config.GuildId, out var guildId)) return;
        var guild = guildId.ToString(CultureInfo.InvariantCulture);
        foreach (var sticky in await _database.ListStickiesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!sticky.Enabled || sticky.GuildId != guild || !ulong.TryParse(sticky.ChannelId, out var channelId) || _client.GetChannel(channelId) is not IMessageChannel channel) continue;
            var exists = ulong.TryParse(sticky.CurrentMessageId, out var messageId)
                && await channel.GetMessageAsync(messageId).ConfigureAwait(false) is not null;
            if (!exists) await RefreshStickyAsync(sticky, cancellationToken).ConfigureAwait(false);
        }
    }

    private double SampleCpuPercentage(Process process)
    {
        lock (_cpuSync)
        {
            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            var elapsed = (now - _lastCpuSampleUtc).TotalMilliseconds;
            var used = (cpu - _lastCpuTime).TotalMilliseconds;
            _lastCpuSampleUtc = now;
            _lastCpuTime = cpu;
            return elapsed <= 0 ? 0 : Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100, 0, 100);
        }
    }

    private async Task SendNotificationAsync(NotificationJob job, CancellationToken cancellationToken)
    {
        try
        {
            using var payload = JsonDocument.Parse(job.PayloadJson);
            var root = payload.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PermanentNotificationException("Invalid notification payload.");
            var titleText = root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : "Jellix";
            var descriptionText = root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String ? description.GetString() : string.Empty;
            var builder = JellixEmbeds.Create(
                Limit(titleText ?? "Jellix", 256),
                Limit(descriptionText ?? string.Empty, 4096),
                JellixEmbeds.Primary,
                NotificationFooter(job.Kind));
            if (root.TryGetProperty("color", out var color) && color.TryGetUInt32(out var rawColor) && rawColor <= 0xFFFFFF) builder.WithColor(new Color(rawColor));
            var destination = job.Destination.Split(':', 2);
            if (destination.Length != 2 || destination[0] is not ("dm" or "channel") || !ulong.TryParse(destination[1], out var id)) throw new PermanentNotificationException("Invalid notification destination.");
            IMessageChannel channel;
            if (destination[0] == "dm")
            {
                IUser user = _client.GetUser(id) ?? (IUser)await _client.Rest.GetUserAsync(id).ConfigureAwait(false);
                channel = await user.CreateDMChannelAsync().ConfigureAwait(false);
            }
            else
            {
                channel = _client.GetChannel(id) as IMessageChannel ?? throw new InvalidOperationException("Notification channel unavailable.");
            }

            var attachmentPath = root.TryGetProperty("attachmentPath", out var attachment) && attachment.ValueKind == JsonValueKind.String
                ? attachment.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(attachmentPath) && File.Exists(attachmentPath))
            {
                builder.WithImageUrl("attachment://" + Path.GetFileName(attachmentPath));
                await channel.SendFileAsync(attachmentPath, embed: builder.Build(), allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
            else
            {
                await channel.SendMessageAsync(embed: builder.Build(), allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            }
            await _database.CompleteNotificationAsync(job.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or PermanentNotificationException or ArgumentException)
        {
            await _database.AbandonNotificationAsync(job.Id, "Invalid notification payload.", cancellationToken).ConfigureAwait(false);
            LogDiscordWarning(_logger, "invalid notification was discarded", exception);
        }
        catch (Exception exception)
        {
            var attempts = job.Attempts + 1;
            if (attempts >= 20)
            {
                await _database.AbandonNotificationAsync(job.Id, "Delivery failed permanently.", cancellationToken).ConfigureAwait(false);
                LogDiscordWarning(_logger, "notification was abandoned after repeated delivery failures", exception);
                return;
            }

            var delay = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(attempts, 10))));
            await _database.RetryNotificationAsync(job.Id, attempts, DateTime.UtcNow.Add(delay), "Delivery failed.", cancellationToken).ConfigureAwait(false);
            LogDiscordWarning(_logger, "notification delivery failed", exception);
        }
    }

    private async Task<UserLink?> RequireLinkAsync(IDiscordInteraction interaction)
    {
        var guildId = interaction.GuildId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var link = await _database.FindLinkByDiscordAsync(guildId, interaction.User.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
        if (link is null)
        {
            await interaction.RespondAsync(DiscordText.T("Verbinde dein Konto zuerst mit `/verbinden`.", "Link your account first with `/link`."), ephemeral: true).ConfigureAwait(false);
        }

        return link;
    }

    private static bool RequireStreamingRole(IDiscordInteraction interaction)
        => RequireRole(interaction, Plugin.Instance?.Configuration.StreamingRoleId, DiscordText.T("Du darfst diesen Befehl nicht verwenden.", "You may not use this command."));

    private static bool RequireConfiguredGuild(IDiscordInteraction interaction)
    {
        if (ulong.TryParse(Plugin.Instance?.Configuration.GuildId, out var guildId) && interaction.GuildId == guildId) return true;
        ObserveResponse(interaction.RespondAsync(DiscordText.T("Dieser Bot ist für diesen Discord-Server nicht konfiguriert.", "This bot is not configured for this Discord server."), ephemeral: true));
        return false;
    }

    private static bool RequireRequestAccess(IDiscordInteraction interaction)
    {
        if (!RequireStreamingRole(interaction)) return false;
        return RequireRole(interaction, Plugin.Instance?.Configuration.RequestRoleId, DiscordText.T("Du darfst keine Anfragen erstellen.", "You may not create requests."));
    }

    private static bool RequireAdmin(IDiscordInteraction interaction)
    {
        if (IsAdmin(interaction.User)) return true;
        ObserveResponse(interaction.RespondAsync(DiscordText.T("Nur Administratoren dürfen diese Aktion verwenden.", "Only administrators may use this action."), ephemeral: true));
        return false;
    }

    private static bool RequireRole(IDiscordInteraction interaction, string? roleId, string error)
    {
        if (string.IsNullOrWhiteSpace(roleId)) return true;
        if (ulong.TryParse(roleId, out var id) && interaction.User is SocketGuildUser user && user.Roles.Any(role => role.Id == id)) return true;
        ObserveResponse(interaction.RespondAsync(error, ephemeral: true));
        return false;
    }

    private static bool IsAdmin(IUser user)
    {
        if (user is not SocketGuildUser guildUser) return false;
        var configured = Plugin.Instance?.Configuration.AdminRoleId;
        return guildUser.GuildPermissions.Administrator
            || (ulong.TryParse(configured, out var id) && guildUser.Roles.Any(role => role.Id == id));
    }

    private bool IsAccessReviewer(SocketUser user, string guildIdValue)
    {
        if (!ulong.TryParse(guildIdValue, out var guildId) || _client.GetGuild(guildId) is not { } guild) return false;
        return guild.OwnerId == user.Id;
    }

    private bool AllowInteraction(IDiscordInteraction interaction, string operation, int limit)
    {
        var key = (interaction.GuildId?.ToString(CultureInfo.InvariantCulture) ?? "dm") + ":" + interaction.User.Id.ToString(CultureInfo.InvariantCulture);
        if (_rateLimiter.TryConsume(key, operation, limit, InteractionRateWindow)) return true;
        ObserveResponse(interaction.RespondAsync(DiscordText.T("Zu viele Aktionen. Bitte warte kurz.", "Too many actions. Please wait a moment."), ephemeral: true));
        return false;
    }

    private static void ObserveResponse(Task response)
        => _ = response.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private async Task TryAssignStreamingRoleAsync(string guildIdValue, string discordIdValue)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.AssignStreamingRoleAfterApproval != true || !ulong.TryParse(config.StreamingRoleId, out var roleId) || !ulong.TryParse(guildIdValue, out var guildId) || !ulong.TryParse(discordIdValue, out var discordId)) return;
        var guild = _client.GetGuild(guildId);
        var role = guild?.GetRole(roleId);
        if (guild is null || role is null) return;
        try
        {
            IGuildUser? user = guild.GetUser(discordId);
            user ??= await _client.Rest.GetGuildUserAsync(guildId, discordId).ConfigureAwait(false);
            if (user is not null && !user.RoleIds.Contains(roleId)) await user.AddRoleAsync(role).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpException or InvalidOperationException)
        {
            LogDiscordWarning(_logger, "streaming role could not be assigned", exception);
        }
    }

    private async Task<bool> TrySendDmAsync(ulong userId, string content)
    {
        try
        {
            IUser user = _client.GetUser(userId) ?? (IUser)await _client.Rest.GetUserAsync(userId).ConfigureAwait(false);
            var channel = await user.CreateDMChannelAsync().ConfigureAwait(false);
            await channel.SendMessageAsync(content, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is HttpException or InvalidOperationException)
        {
            LogDiscordWarning(_logger, "direct message could not be delivered", exception);
            return false;
        }
    }

    private async Task<bool> TrySendDmAsync(ulong userId, Embed embed, MessageComponent components)
    {
        try
        {
            IUser user = _client.GetUser(userId) ?? (IUser)await _client.Rest.GetUserAsync(userId).ConfigureAwait(false);
            var channel = await user.CreateDMChannelAsync().ConfigureAwait(false);
            await channel.SendMessageAsync(embed: embed, components: components, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is HttpException or InvalidOperationException)
        {
            LogDiscordWarning(_logger, "direct message embed could not be delivered", exception);
            return false;
        }
    }

    private static async Task RespondErrorAsync(IDiscordInteraction interaction, string error)
    {
        if (interaction.HasResponded) await interaction.FollowupAsync(error, ephemeral: true).ConfigureAwait(false);
        else await interaction.RespondAsync(error, ephemeral: true).ConfigureAwait(false);
    }

    private async Task TryWriteAuditAsync(string actorType, string actorId, string action, string targetType, string targetId, string details)
    {
        try
        {
            await _database.WriteAuditAsync(actorType, actorId, action, targetType, targetId, true, details, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogDiscordWarning(_logger, "an audit entry could not be written", exception);
        }
    }

    private static string GetString(SocketSlashCommand command, string name, string fallback = "")
        => command.Data.Options.FirstOrDefault(value => value.Name == name)?.Value?.ToString() ?? fallback;

    private static bool GetBoolean(SocketSlashCommand command, string name)
        => command.Data.Options.FirstOrDefault(value => value.Name == name)?.Value is bool value && value;

    private static long? GetLong(SocketSlashCommand command, string name)
        => command.Data.Options.FirstOrDefault(value => value.Name == name)?.Value is long value ? value : null;

    private static double? GetDouble(SocketSlashCommand command, string name)
        => command.Data.Options.FirstOrDefault(value => value.Name == name)?.Value is double value ? value : null;

    private static DateTime? PeriodStartUtc(string period)
    {
        if (period == "all") return null;
        var zone = ResolveTimeZone();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        var localStart = period switch
        {
            "today" => localNow.Date,
            "week" => localNow.Date.AddDays(-(((int)localNow.DayOfWeek + 6) % 7)),
            "year" => new DateTime(localNow.Year, 1, 1),
            _ => new DateTime(localNow.Year, localNow.Month, 1),
        };
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), zone);
    }

    private static TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            var id = Plugin.Instance?.Configuration.TimeZoneId;
            return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    private static string FormatDuration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalDays >= 1
            ? DiscordText.T($"{(long)span.TotalDays} Tage {span.Hours} Stunden", $"{(long)span.TotalDays} days {span.Hours} hours")
            : $"{(long)span.TotalHours}h {span.Minutes}m";
    }

    private static string FormatPosition(long? positionTicks, long? runtimeTicks)
    {
        var position = TimeSpan.FromTicks(Math.Max(0, positionTicks.GetValueOrDefault()));
        var runtime = TimeSpan.FromTicks(Math.Max(0, runtimeTicks.GetValueOrDefault()));
        return $"{position:hh\\:mm\\:ss} / {runtime:hh\\:mm\\:ss}";
    }

    private static string FormatRequest(JsonElement item)
    {
        var title = item.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String ? titleValue.GetString() ?? "MediaForge" : "MediaForge";
        var status = item.TryGetProperty("status", out var statusValue) && statusValue.ValueKind == JsonValueKind.String ? statusValue.GetString() ?? "pending" : "pending";
        status = Limit(status.ToLowerInvariant(), 64);
        var progress = item.TryGetProperty("progress", out var progressValue) && progressValue.TryGetInt32(out var number) ? $" – {Math.Clamp(number, 0, 100)}%" : string.Empty;
        var icon = status switch { "available" => "🟢", "completed" => "🟢", "queued" => "🔵", "downloading" => "🔵", "rejected" => "🔴", "failed" => "🔴", _ => "🟡" };
        var label = status switch
        {
            "available" or "completed" => DiscordText.T("Verfügbar", "Available"),
            "downloading" => DiscordText.T("Wird heruntergeladen", "Downloading"),
            "queued" => DiscordText.T("In Warteschlange", "Queued"),
            "rejected" => DiscordText.T("Abgelehnt", "Rejected"),
            "failed" => DiscordText.T("Fehlgeschlagen", "Failed"),
            "pending" or "processing" => DiscordText.T("Wartet auf Freigabe", "Waiting for approval"),
            _ => status,
        };
        return $"{icon} **{Escape(Limit(title, 160))}**\n{Escape(Limit(label, 100))}{progress}";
    }

    private static Embed BuildRequestsEmbed(string[] lines)
        => JellixEmbeds.Create(
                "📋 " + DiscordText.T("Deine MediaForge-Anfragen", "Your MediaForge requests"),
                lines.Length == 0 ? DiscordText.T("Noch keine Anfragen vorhanden.", "No requests found yet.") : JoinWithinLimit(lines, "\n\n", 4096),
                JellixEmbeds.Secondary,
                DiscordText.T("Status direkt aus MediaForge", "Status directly from MediaForge"))
            .Build();

    private static string PeriodLabel(string period)
        => period switch
        {
            "today" => DiscordText.T("Heute", "Today"),
            "week" => DiscordText.T("Diese Woche", "This week"),
            "month" => DiscordText.T("Dieser Monat", "This month"),
            "year" => DiscordText.T("Dieses Jahr", "This year"),
            _ => DiscordText.T("Gesamt", "All time"),
        };

    private static string LeaderboardCategoryLabel(string category)
        => category switch
        {
            "movies" => DiscordText.T("Filme", "Movies"),
            "series" => DiscordText.T("Serien", "Series"),
            "episodes" => DiscordText.T("Episoden", "Episodes"),
            _ => "Watchtime",
        };

    private static string NotificationFooter(string kind)
        => kind switch
        {
            "achievement" => DiscordText.T("Jellix Achievement", "Jellix achievement"),
            "library" => DiscordText.T("Neu in deiner Jellyfin-Bibliothek", "New in your Jellyfin library"),
            "mediaforge-request" => "Jellix • MediaForge",
            "admin-alert" => DiscordText.T("Jellix Systemwarnung", "Jellix system alert"),
            _ => DiscordText.T("Jellix für Jellyfin", "Jellix for Jellyfin"),
        };

    private static string AchievementName(string id)
        => id switch
        {
            "film-fan" => "🍿 Filmfan",
            "cineaste" => "🎬 Cineast",
            "series-junkie" => DiscordText.T("📺 Serienjunkie", "📺 Series junkie"),
            "night-owl" => DiscordText.T("🌙 Nachteule", "🌙 Night owl"),
            "binge-watcher" => "🔥 Binge Watcher",
            "no-life" => "💀 No Life",
            _ => Escape(id),
        };

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal).Replace("~", "\\~", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);

    private static string Limit(string value, int length)
        => value.Length <= length ? value : value[..(length - 1)] + "…";

    private static string JoinWithinLimit(IEnumerable<string> values, string separator, int maximum)
    {
        var result = new StringBuilder();
        foreach (var value in values)
        {
            var prefixLength = result.Length == 0 ? 0 : separator.Length;
            if (result.Length + prefixLength + value.Length > maximum)
            {
                if (result.Length == 0) result.Append(Limit(value, maximum));
                break;
            }

            if (prefixLength > 0) result.Append(separator);
            result.Append(value);
        }

        return result.ToString();
    }

    private static string Check(bool value) => value ? "✅" : "❌";

    private sealed class PermanentNotificationException : Exception
    {
        public PermanentNotificationException(string message)
            : base(message)
        {
        }
    }
}
