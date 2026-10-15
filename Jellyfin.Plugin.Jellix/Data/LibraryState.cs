using Jellyfin.Plugin.Jellix.Models;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Jellix.Data;

public sealed partial class JellixDatabase
{
    private const string LibraryStateSchema = """
        CREATE TABLE IF NOT EXISTS LibrarySeriesKeys(SeriesId TEXT PRIMARY KEY, StableKey TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS LibraryParentTriggers(ParentId TEXT PRIMARY KEY, FirstSeenUtc TEXT NOT NULL, Consumed INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS LibrarySeriesBatches(
          Id TEXT PRIMARY KEY, SeriesKey TEXT NOT NULL, SeriesId TEXT NOT NULL, ChannelId TEXT NOT NULL,
          CreatedUtc TEXT NOT NULL, LastAddedUtc TEXT NOT NULL, Revision INTEGER NOT NULL DEFAULT 0,
          QueuedRevision INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS IX_SeriesBatches_Series ON LibrarySeriesBatches(SeriesKey, ChannelId, LastAddedUtc);
        CREATE TABLE IF NOT EXISTS LibrarySeriesMembers(
          Identity TEXT PRIMARY KEY, BatchId TEXT NOT NULL REFERENCES LibrarySeriesBatches(Id), ItemId TEXT NOT NULL, EpisodeNumber INTEGER NULL
        );
        CREATE INDEX IF NOT EXISTS IX_SeriesMembers_Batch ON LibrarySeriesMembers(BatchId);
        CREATE TABLE IF NOT EXISTS ManagedMessages(
          MessageKey TEXT PRIMARY KEY, ChannelId TEXT NOT NULL, MessageId TEXT NULL,
          ContentHash TEXT NOT NULL DEFAULT '', AttemptedUtc TEXT NOT NULL
        );
        """;

    private static async Task MigrateLibraryOutboxAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Only run once, before the Discord worker starts. Old jobs only contain
        // presentation JSON, not item IDs; their dedupe keys retain the legacy identities.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LibraryAnnouncementState SET AnnouncedUtc=NULL
            WHERE ItemId IN (
              SELECT identity.ItemId FROM LibraryAnnouncementIdentity AS identity
              JOIN NotificationOutbox AS job ON job.DedupeKey='library:' || identity.Identity
              WHERE job.Kind='library' AND job.CompletedUtc IS NULL
            ) AND NOT EXISTS(SELECT 1 FROM BotState WHERE Key='library-grouped-outbox-v1');
            UPDATE LibraryAnnouncementIdentity SET AnnouncedUtc=NULL
            WHERE Identity IN (
              SELECT substr(DedupeKey, 9) FROM NotificationOutbox WHERE Kind='library' AND CompletedUtc IS NULL
            ) AND NOT EXISTS(SELECT 1 FROM BotState WHERE Key='library-grouped-outbox-v1');
            UPDATE NotificationOutbox SET CompletedUtc=$now, LastError='Migrated to grouped library processing.'
            WHERE Kind='library' AND CompletedUtc IS NULL
              AND NOT EXISTS(SELECT 1 FROM BotState WHERE Key='library-grouped-outbox-v1');
            INSERT OR IGNORE INTO BotState(Key, Value, UpdatedUtc) VALUES('library-grouped-outbox-v1', '1', $now);
            """;
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> ResolveSeriesKeyAsync(Guid seriesId, string proposed, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO LibrarySeriesKeys(SeriesId, StableKey) VALUES($id, $key); SELECT StableKey FROM LibrarySeriesKeys WHERE SeriesId=$id;";
        command.Parameters.AddWithValue("$id", seriesId.ToString("N"));
        command.Parameters.AddWithValue("$key", proposed);
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    internal async Task<HashSet<Guid>> ListKnownLibraryIdsAsync(CancellationToken cancellationToken)
    {
        var result = new HashSet<Guid>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ItemId FROM LibraryAnnouncementState;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    internal async Task RestorePendingLibraryItemsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE LibraryAnnouncementState SET AnnouncedUtc=NULL WHERE ItemId=$item; UPDATE LibraryAnnouncementIdentity SET AnnouncedUtc=NULL WHERE ItemId=$item;";
        var item = command.Parameters.Add("$item", SqliteType.Text);
        foreach (var id in itemIds)
        {
            item.Value = id.ToString("N");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task RegisterParentTriggerAsync(Guid parentId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO LibraryParentTriggers(ParentId, FirstSeenUtc) VALUES($id, $now);";
        command.Parameters.AddWithValue("$id", parentId.ToString("N"));
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> HasParentTriggerAsync(Guid seriesId, Guid seasonId, DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM LibraryParentTriggers WHERE ParentId IN ($series, $season) AND (Consumed=0 OR FirstSeenUtc>=$cutoff) LIMIT 1;";
        command.Parameters.AddWithValue("$series", seriesId.ToString("N"));
        command.Parameters.AddWithValue("$season", seasonId.ToString("N"));
        command.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    internal async Task RebindSeriesMemberAsync(string identity, Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LibrarySeriesMembers SET ItemId=$item WHERE Identity=$identity;";
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task ConsumeParentTriggersAsync(Guid seriesId, Guid seasonId, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LibraryParentTriggers SET Consumed=1, FirstSeenUtc=$now WHERE ParentId IN ($series, $season) AND Consumed=0;";
        command.Parameters.AddWithValue("$series", seriesId.ToString("N"));
        command.Parameters.AddWithValue("$season", seasonId.ToString("N"));
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> HasActiveSeriesBatchAsync(string seriesKey, string channelId, DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM LibrarySeriesBatches WHERE SeriesKey=$series AND ChannelId=$channel AND LastAddedUtc>=$cutoff LIMIT 1;";
        command.Parameters.AddWithValue("$series", seriesKey);
        command.Parameters.AddWithValue("$channel", channelId);
        command.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    internal async Task AcknowledgeSeriesBatchAsync(SeriesBatch batch, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE LibrarySeriesBatches SET QueuedRevision=$revision WHERE Id=$batch;";
        command.Parameters.AddWithValue("$batch", batch.Id);
        command.Parameters.AddWithValue("$revision", batch.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task AddSeriesMemberAsync(string seriesKey, Guid seriesId, string channelId, string identity, Guid itemId, DateTime now, TimeSpan mergeWindow, CancellationToken cancellationToken, int? episodeNumber = null)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM LibrarySeriesMembers WHERE Identity=$identity;";
        command.Parameters.AddWithValue("$identity", identity);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            // Keep the most recent Jellyfin ID when an item was recreated.
            command.CommandText = "UPDATE LibrarySeriesMembers SET ItemId=$item WHERE Identity=$identity;";
            command.Parameters.AddWithValue("$item", itemId.ToString("N"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        command.CommandText = "SELECT Id FROM LibrarySeriesBatches WHERE SeriesKey=$series AND ChannelId=$channel AND LastAddedUtc>=$cutoff ORDER BY LastAddedUtc DESC LIMIT 1;";
        command.Parameters.AddWithValue("$series", seriesKey);
        command.Parameters.AddWithValue("$channel", channelId);
        command.Parameters.AddWithValue("$cutoff", FormatUtc(now.Subtract(mergeWindow)));
        var batchId = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? Guid.NewGuid().ToString("N");
        command.CommandText = """
            INSERT INTO LibrarySeriesBatches(Id, SeriesKey, SeriesId, ChannelId, CreatedUtc, LastAddedUtc, Revision)
            VALUES($batch, $series, $seriesId, $channel, $now, $now, 1)
            ON CONFLICT(Id) DO UPDATE SET LastAddedUtc=$now, SeriesId=$seriesId, Revision=Revision+1;
            INSERT INTO LibrarySeriesMembers(Identity, BatchId, ItemId, EpisodeNumber) VALUES($identity, $batch, $item, $number);
            """;
        command.Parameters.AddWithValue("$batch", batchId);
        command.Parameters.AddWithValue("$seriesId", seriesId.ToString("N"));
        command.Parameters.AddWithValue("$now", FormatUtc(now));
        command.Parameters.AddWithValue("$item", itemId.ToString("N"));
        command.Parameters.AddWithValue("$number", (object?)episodeNumber ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<SeriesBatch>> ListReadySeriesBatchesAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        var result = new List<SeriesBatch>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SeriesKey, SeriesId, ChannelId, CreatedUtc, LastAddedUtc, Revision FROM LibrarySeriesBatches WHERE Revision>QueuedRevision AND LastAddedUtc<=$cutoff ORDER BY LastAddedUtc;";
        command.Parameters.AddWithValue("$cutoff", FormatUtc(cutoff));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new SeriesBatch(reader.GetString(0), reader.GetString(1), Guid.Parse(reader.GetString(2)), reader.GetString(3), ParseUtc(reader.GetString(4)), ParseUtc(reader.GetString(5)), reader.GetInt32(6)));
        return result;
    }

    internal async Task<IReadOnlyList<SeriesBatchMember>> ListSeriesMembersAsync(string batchId, CancellationToken cancellationToken)
    {
        var result = new List<SeriesBatchMember>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Identity, ItemId, EpisodeNumber FROM LibrarySeriesMembers WHERE BatchId=$batch;";
        command.Parameters.AddWithValue("$batch", batchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(new SeriesBatchMember(reader.GetString(0), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        return result;
    }

    internal async Task QueueSeriesBatchAsync(SeriesBatch batch, string payload, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertManagedJobAsync(connection, transaction, "library-series", batch.ChannelId, payload, "series:" + batch.Id, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE LibrarySeriesBatches SET QueuedRevision=$revision WHERE Id=$batch;";
        command.Parameters.AddWithValue("$batch", batch.Id);
        command.Parameters.AddWithValue("$revision", batch.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task QueueOverviewAsync(string channel, string payload, string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await UpsertManagedJobAsync(connection, null, "library-overview", channel, payload, key, cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertManagedJobAsync(SqliteConnection connection, SqliteTransaction? transaction, string kind, string channel, string payload, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO NotificationOutbox(Priority, Kind, Destination, PayloadJson, DedupeKey, AvailableUtc)
            VALUES(2, $kind, $destination, $payload, $key, $now)
            ON CONFLICT(DedupeKey) DO UPDATE SET PayloadJson=$payload, CompletedUtc=NULL, Attempts=0, LastError=NULL, AvailableUtc=$now
            WHERE NotificationOutbox.PayloadJson<>$payload OR NotificationOutbox.CompletedUtc IS NOT NULL;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$destination", "channel:" + channel);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ManagedMessageRecord?> GetManagedMessageAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ChannelId, MessageId, ContentHash, AttemptedUtc FROM ManagedMessages WHERE MessageKey=$key;";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new ManagedMessageRecord(key, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), ParseUtc(reader.GetString(3))) : null;
    }

    internal async Task SaveManagedMessageAsync(ManagedMessageRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ManagedMessages(MessageKey, ChannelId, MessageId, ContentHash, AttemptedUtc)
            VALUES($key, $channel, $message, $hash, $attempt)
            ON CONFLICT(MessageKey) DO UPDATE SET ChannelId=$channel, MessageId=$message, ContentHash=$hash, AttemptedUtc=$attempt;
            """;
        command.Parameters.AddWithValue("$key", record.Key);
        command.Parameters.AddWithValue("$channel", record.ChannelId);
        command.Parameters.AddWithValue("$message", (object?)record.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$hash", record.ContentHash);
        command.Parameters.AddWithValue("$attempt", FormatUtc(record.AttemptedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CompleteManagedNotificationAsync(NotificationJob job, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE NotificationOutbox SET CompletedUtc=$now, LastError=NULL WHERE Id=$id AND PayloadJson=$payload;";
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$payload", job.PayloadJson);
        command.Parameters.AddWithValue("$now", FormatUtc(DateTime.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
