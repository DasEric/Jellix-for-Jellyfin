using System.Collections.Concurrent;
using System.Threading.Channels;
using Jellyfin.Plugin.Jellix.Data;
using Jellyfin.Plugin.Jellix.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellix.Services;

/// <summary>Captures actual watched time without counting pauses or seek jumps.</summary>
public sealed class PlaybackTrackingService : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogPersistenceError = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1001, "PlaybackPersistenceFailed"),
        "Jellix could not persist a playback update");
    private readonly ISessionManager _sessionManager;
    private readonly JellixDatabase _database;
    private readonly AchievementService _achievements;
    private readonly ILogger<PlaybackTrackingService> _logger;
    private readonly ConcurrentDictionary<string, ActivePlayback> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _fallbackSessionKeys = new(StringComparer.Ordinal);
    private readonly Channel<PlaybackRecord> _writes = Channel.CreateUnbounded<PlaybackRecord>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    });
    private bool _ready;

    public PlaybackTrackingService(
        ISessionManager sessionManager,
        JellixDatabase database,
        AchievementService achievements,
        ILogger<PlaybackTrackingService> logger)
    {
        _sessionManager = sessionManager;
        _database = database;
        _achievements = achievements;
        _logger = logger;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _database.RecoverInterruptedPlaybackAsync(cancellationToken).ConfigureAwait(false);
            _ready = true;
        }
        catch (Exception exception)
        {
            LogPersistenceError(_logger, exception);
            await base.StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _sessionManager.PlaybackStart += OnPlaybackStart;
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlaybackStart;
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        foreach (var state in _active.Values)
        {
            lock (state.Sync)
            {
                state.EndedUtc = DateTime.UtcNow;
                _writes.Writer.TryWrite(state.Snapshot());
            }
        }

        _writes.Writer.TryComplete();
        if (ExecuteTask is not null)
        {
            await ExecuteTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_ready) return;
        await foreach (var record in _writes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await _database.UpsertPlaybackAsync(record, stoppingToken).ConfigureAwait(false);
                        break;
                    }
                    catch (SqliteException) when (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), stoppingToken).ConfigureAwait(false);
                    }
                }

                if (record.Completed)
                {
                    await _achievements.EvaluateAsync(record.JellyfinUserId, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogPersistenceError(_logger, exception);
            }
        }
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs args)
    {
        if (Plugin.Instance?.Configuration.StatisticsEnabled != true || !TryMedia(args, out var media))
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var user in args.Users)
        {
            var key = BuildKey(args, user.Id, newSession: true);
            var state = new ActivePlayback(
                key,
                user.Id,
                args.Item.Id,
                media.Type,
                media.SeriesId,
                args.Item.Name ?? string.Empty,
                media.SeriesName,
                now,
                args.PlaybackPositionTicks.GetValueOrDefault(),
                args.Item.RunTimeTicks.GetValueOrDefault(),
                args.IsPaused,
                args.DeviceName ?? string.Empty);
            _active.GetOrAdd(key, state);
        }
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs args)
        => Update(args, stopped: false, playedToCompletion: false);

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs args)
        => Update(args, stopped: true, args.PlayedToCompletion);

    private void Update(PlaybackProgressEventArgs args, bool stopped, bool playedToCompletion)
    {
        if (!TryMedia(args, out var media))
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var user in args.Users)
        {
            var key = BuildKey(args, user.Id, newSession: false);
            if (!_active.TryGetValue(key, out var state))
            {
                if (Plugin.Instance?.Configuration.StatisticsEnabled != true || stopped)
                {
                    continue;
                }

                state = _active.GetOrAdd(key, _ => new ActivePlayback(
                    key,
                    user.Id,
                    args.Item.Id,
                    media.Type,
                    media.SeriesId,
                    args.Item.Name ?? string.Empty,
                    media.SeriesName,
                    now,
                    args.PlaybackPositionTicks.GetValueOrDefault(),
                    args.Item.RunTimeTicks.GetValueOrDefault(),
                    args.IsPaused,
                    args.DeviceName ?? string.Empty));
            }

            lock (state.Sync)
            {
                if (state.EndedUtc.HasValue)
                {
                    continue;
                }

                var position = Math.Max(0, args.PlaybackPositionTicks.GetValueOrDefault());
                var acceptedSeconds = PlaybackTimeCalculator.AcceptedSeconds(
                    state.LastPositionTicks,
                    position,
                    (now - state.LastEventUtc).TotalSeconds,
                    state.WasPaused,
                    args.IsPaused);
                state.ActualWatchSeconds += acceptedSeconds;
                state.NightWatchSeconds += JellixDatabase.CalculateNightSeconds(now.AddSeconds(-acceptedSeconds), acceptedSeconds, ResolveTimeZone());

                state.LastPositionTicks = position;
                state.LastEventUtc = now;
                state.WasPaused = args.IsPaused;
                state.DeviceName = args.DeviceName ?? state.DeviceName;
                var threshold = Math.Clamp(Plugin.Instance?.Configuration.CompletedPlaybackPercent ?? 90, 50, 100) / 100d;
                var wasCompleted = state.Completed;
                state.Completed |= playedToCompletion
                    || (state.RuntimeTicks > 0 && position >= state.RuntimeTicks * threshold);
                if (!wasCompleted && state.Completed) state.CompletedUtc = now;
                if (stopped)
                {
                    state.EndedUtc = now;
                }

                _writes.Writer.TryWrite(state.Snapshot());
            }

            if (stopped)
            {
                _active.TryRemove(key, out _);
                RemoveFallbackKey(args, user.Id);
            }
        }
    }

    private string BuildKey(PlaybackProgressEventArgs args, Guid userId, bool newSession)
    {
        if (!string.IsNullOrWhiteSpace(args.PlaySessionId))
        {
            return args.PlaySessionId + ":" + userId.ToString("N") + ":" + args.Item.Id.ToString("N");
        }

        var session = !string.IsNullOrWhiteSpace(args.Session?.Id) ? args.Session.Id : args.DeviceId ?? "unknown-device";
        var fallbackIdentity = session + ":" + userId.ToString("N") + ":" + args.Item.Id.ToString("N");
        if (newSession)
        {
            return _fallbackSessionKeys.GetOrAdd(fallbackIdentity, static value => value + ":" + Guid.NewGuid().ToString("N"));
        }

        return _fallbackSessionKeys.GetOrAdd(fallbackIdentity, static value => value + ":" + Guid.NewGuid().ToString("N"));
    }

    private void RemoveFallbackKey(PlaybackProgressEventArgs args, Guid userId)
    {
        if (!string.IsNullOrWhiteSpace(args.PlaySessionId)) return;
        var session = !string.IsNullOrWhiteSpace(args.Session?.Id) ? args.Session.Id : args.DeviceId ?? "unknown-device";
        _fallbackSessionKeys.TryRemove(session + ":" + userId.ToString("N") + ":" + args.Item.Id.ToString("N"), out _);
    }

    private static bool TryMedia(PlaybackProgressEventArgs args, out MediaIdentity media)
    {
        switch (args.Item)
        {
            case Movie:
                media = new MediaIdentity("Movie", null, null);
                return true;
            case Episode episode:
                media = new MediaIdentity("Episode", episode.SeriesId == Guid.Empty ? null : episode.SeriesId, episode.SeriesName);
                return true;
            default:
                media = default;
                return false;
        }
    }

    private readonly record struct MediaIdentity(string Type, Guid? SeriesId, string? SeriesName);

    private sealed class ActivePlayback
    {
        public ActivePlayback(string sessionKey, Guid userId, Guid itemId, string itemType, Guid? seriesId, string itemName, string? seriesName, DateTime startedUtc, long lastPositionTicks, long runtimeTicks, bool wasPaused, string deviceName)
        {
            SessionKey = sessionKey;
            UserId = userId;
            ItemId = itemId;
            ItemType = itemType;
            SeriesId = seriesId;
            ItemName = itemName;
            SeriesName = seriesName;
            StartedUtc = startedUtc;
            LastEventUtc = startedUtc;
            LastPositionTicks = lastPositionTicks;
            RuntimeTicks = runtimeTicks;
            WasPaused = wasPaused;
            DeviceName = deviceName;
        }

        public object Sync { get; } = new();
        public string SessionKey { get; }
        public Guid UserId { get; }
        public Guid ItemId { get; }
        public string ItemType { get; }
        public Guid? SeriesId { get; }
        public string ItemName { get; }
        public string? SeriesName { get; }
        public DateTime StartedUtc { get; }
        public DateTime? EndedUtc { get; set; }
        public double ActualWatchSeconds { get; set; }
        public double NightWatchSeconds { get; set; }
        public long RuntimeTicks { get; }
        public long LastPositionTicks { get; set; }
        public DateTime LastEventUtc { get; set; }
        public bool WasPaused { get; set; }
        public bool Completed { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public string DeviceName { get; set; }

        public PlaybackRecord Snapshot()
            => new(SessionKey, UserId, ItemId, ItemType, SeriesId, ItemName, SeriesName, StartedUtc, EndedUtc, (long)Math.Floor(ActualWatchSeconds), (long)Math.Floor(NightWatchSeconds), RuntimeTicks, LastPositionTicks, LastEventUtc, Completed, CompletedUtc, DeviceName);
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
}
