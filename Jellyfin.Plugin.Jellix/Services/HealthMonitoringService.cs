using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Discord;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Tracks Discord, scheduled library scans and optional Jellyfin updates.</summary>
public sealed class HealthMonitoringService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(3201, "HealthMonitorFailed"),
        "Jellix health monitoring failed");
    private readonly JellixDatabase _database;
    private readonly DiscordBotService _discord;
    private readonly ITaskManager _taskManager;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HealthMonitoringService> _logger;
    private readonly Channel<string> _libraryFailures = Channel.CreateUnbounded<string>();
    private DateTime _lastUpdateAttemptUtc = DateTime.MinValue;
    private bool _ready;

    public HealthMonitoringService(
        JellixDatabase database,
        DiscordBotService discord,
        ITaskManager taskManager,
        IServerApplicationHost applicationHost,
        IHttpClientFactory httpClientFactory,
        ILogger<HealthMonitoringService> logger)
    {
        _database = database;
        _discord = discord;
        _taskManager = taskManager;
        _applicationHost = applicationHost;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _ready = true;
        }
        catch (Exception exception)
        {
            LogFailure(_logger, exception);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _taskManager.TaskCompleted += OnTaskCompleted;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        _libraryFailures.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_ready) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (_libraryFailures.Reader.TryRead(out var taskName))
                {
                    await QueueOneOffAlertAsync($"Der Bibliotheksscan „{taskName}“ ist fehlgeschlagen.", $"The library scan “{taskName}” failed.", "library-scan:" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture), stoppingToken).ConfigureAwait(false);
                }

                var config = Plugin.Instance?.Configuration;
                if (config?.BotEnabled == true && Plugin.Instance?.Secrets.HasToken == true)
                {
                    var disconnected = !_discord.IsReady;
                    await AlertAsync(
                        "discord-disconnected",
                        disconnected,
                        disconnected ? "Die Discord-Verbindung ist unterbrochen." : "Die Discord-Verbindung wurde wiederhergestellt.",
                        disconnected ? "The Discord connection is down." : "The Discord connection has recovered.",
                        stoppingToken).ConfigureAwait(false);
                }

                if (config?.CheckJellyfinUpdates == true)
                {
                    await CheckUpdateAsync(stoppingToken).ConfigureAwait(false);
                }

                await _database.PruneAsync(config?.AuditRetentionDays ?? 180, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailure(_logger, exception);
            }

            var minutes = Math.Clamp(Plugin.Instance?.Configuration.HealthCheckMinutes ?? 5, 1, 1440);
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken).ConfigureAwait(false);
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs eventArgs)
    {
        var taskType = eventArgs.Task.ScheduledTask.GetType().Name;
        var isLibraryScan = taskType.Contains("RefreshMediaLibrary", StringComparison.OrdinalIgnoreCase)
            || (eventArgs.Result.Key?.Contains("RefreshLibrary", StringComparison.OrdinalIgnoreCase) ?? false);
        if (isLibraryScan && eventArgs.Result.Status == TaskCompletionStatus.Failed)
        {
            _libraryFailures.Writer.TryWrite(eventArgs.Task.Name);
        }
    }

    private async Task CheckUpdateAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (now - _lastUpdateAttemptUtc < TimeSpan.FromMinutes(15)) return;
        var last = await _database.GetBotStateAsync("jellyfin-update-last-check", cancellationToken).ConfigureAwait(false);
        if (DateTime.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastUtc)
            && now - lastUtc.ToUniversalTime() < TimeSpan.FromHours(6))
        {
            return;
        }

        _lastUpdateAttemptUtc = now;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/jellyfin/jellyfin/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Jellix", Plugin.Instance?.Version.ToString(3) ?? "0.1.0"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _httpClientFactory.CreateClient("JellixUpdates").SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var tag = document.RootElement.TryGetProperty("tag_name", out var tagValue) ? tagValue.GetString()?.TrimStart('v') : null;
        if (!Version.TryParse(tag, out var latest))
        {
            return;
        }

        await _database.SetBotStateAsync("jellyfin-update-last-check", now.ToString("O", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);

        var updateAvailable = latest > _applicationHost.ApplicationVersion;
        await AlertAsync(
            "jellyfin-update-available",
            updateAvailable,
            updateAvailable ? $"Jellyfin {latest} ist verfügbar (installiert: {_applicationHost.ApplicationVersion})." : "Jellyfin ist aktuell.",
            updateAvailable ? $"Jellyfin {latest} is available (installed: {_applicationHost.ApplicationVersion})." : "Jellyfin is up to date.",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AlertAsync(string key, bool active, string german, string english, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var destination = _discord.ResolveOwnerDestination();
        if (config?.AdminAlertsEnabled != true || destination is null)
        {
            return;
        }

        if (!await _database.SetHealthIncidentAsync(key, active, DateTime.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            title = active ? "⚠️ Jellix" : "✅ Jellix",
            description = config.Language == "en" ? english : german,
            color = active ? 0xE74C3Cu : 0x2ECC71u,
        });
        await _database.EnqueueNotificationAsync(NotificationPriority.High, "admin-alert", destination, payload, $"health:{key}:{active}:{DateTime.UtcNow:yyyyMMddHHmmss}", DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    private async Task QueueOneOffAlertAsync(string german, string english, string dedupe, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var destination = _discord.ResolveOwnerDestination();
        if (config?.AdminAlertsEnabled != true || destination is null) return;
        var payload = JsonSerializer.Serialize(new { title = "⚠️ Jellix", description = config.Language == "en" ? english : german, color = 0xE74C3Cu });
        await _database.EnqueueNotificationAsync(NotificationPriority.High, "admin-alert", destination, payload, dedupe, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
    }
}
