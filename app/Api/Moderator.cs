using System.Diagnostics;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.OutputCaching;

namespace Api;

// ---------------------------------------------------------------------------
// Takes each new entry's ID off the queue and moderates it: an entry is
// approved, unless its message mentions "spam", in which case it's rejected.
//
// Each entry is moderated in its own trace span, so in Application Insights
// (or the Aspire dashboard) you can see the queue being worked through.
// ---------------------------------------------------------------------------
public class Moderator(QueueClient queue, BlobContainerClient container, IOutputCacheStore cache,
    IHostEnvironment environment, ILogger<Moderator> logger) : BackgroundService
{
    private readonly ActivitySource _activitySource = new(environment.ApplicationName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = (await queue.ReceiveMessagesAsync(maxMessages: 16, cancellationToken: stoppingToken)).Value;
                foreach (var message in messages)
                {
                    await ModerateAsync(Guid.Parse(message.MessageText), stoppingToken);
                    await queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, stoppingToken);
                }

                if (messages.Length == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A message that fails is left on the queue, and is received
                // again once its visibility timeout runs out.
                logger.LogError(ex, "Moderating entries failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ModerateAsync(Guid id, CancellationToken cancellationToken)
    {
        using var activity = _activitySource.StartActivity("moderate entry");
        activity?.SetTag("entry.id", id);

        var blob = container.GetBlobClient($"{id}.json");
        var entry = (await blob.DownloadContentAsync(cancellationToken)).Value.Content.ToObjectFromJson<Entry>()!;

        var state = entry.Message.Contains("spam", StringComparison.OrdinalIgnoreCase)
            ? EntryState.Rejected
            : EntryState.Approved;
        activity?.SetTag("entry.state", state.ToString());

        // Leave a moment between an entry being added and moderated, so the
        // page shows it Pending before it changes.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);

        await blob.UploadAsync(BinaryData.FromObjectAsJson(entry with { State = state, ModeratedAt = DateTimeOffset.UtcNow }),
            overwrite: true, cancellationToken);
        await cache.EvictByTagAsync(Entry.CacheTag, cancellationToken);

        logger.LogInformation("Entry {EntryId} from {Name} was {State}", id, entry.Name, state);
    }
}
