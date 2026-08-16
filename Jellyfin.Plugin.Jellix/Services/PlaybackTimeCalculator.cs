namespace Jellyfin.Plugin.Jellix.Services;

internal static class PlaybackTimeCalculator
{
    public static double AcceptedSeconds(long previousPositionTicks, long currentPositionTicks, double wallSeconds, bool wasPaused, bool isPaused)
    {
        _ = isPaused; // The transition into pause still contains the preceding played interval.
        if (wasPaused || currentPositionTicks <= previousPositionTicks)
        {
            return 0;
        }

        var positionSeconds = (currentPositionTicks - previousPositionTicks) / (double)TimeSpan.TicksPerSecond;
        var boundedWallSeconds = Math.Clamp(wallSeconds, 0, 300);
        if (boundedWallSeconds <= 0)
        {
            return 0;
        }

        // Count elapsed viewing time, not media timeline movement. This keeps
        // playback speeds and forward seeks from inflating statistics. A very
        // small position change indicates buffering or a stalled player.
        return positionSeconds < boundedWallSeconds * 0.25
            ? Math.Max(0, positionSeconds)
            : boundedWallSeconds;
    }
}
