using Api;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.OutputCaching;

// ---------------------------------------------------------------------------
// The api: stores guestbook entries as blobs, queues each new one for the
// moderator, and serves the list of entries from a Redis output cache.
//
// Each Add* call below takes the name of a resource in the AppHost. The AppHost
// hands this app a connection string for each one (ConnectionStrings__<name>),
// and the Aspire client integration builds the client, its health check and
// its telemetry from it.
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Who the api signs in to Azure as: its managed identity on App Service, or,
// run on your machine under `locally run`, the identity `locally run` provides.
// UseLocallyCredential() makes both work against Locally: it turns off the
// instance discovery Azure.Identity would otherwise do against
// login.microsoftonline.com, and signs the Redis client in to Locally's cache
// with a Microsoft Entra token rather than an access key.
builder.UseLocallyCredential();

builder.AddAzureBlobContainerClient("entries");
builder.AddAzureQueue("submissions");
builder.AddRedisClientBuilder("cache").WithOutputCache();

builder.Services.AddHostedService<Moderator>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseOutputCache();

// Lists the entries, newest first. The response is cached in Redis for 30
// seconds, or until an entry is added or moderated, which evicts it.
app.MapGet("/entries", async (BlobContainerClient container, CancellationToken cancellationToken) =>
{
    var entries = new List<Entry>();
    await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken))
    {
        var content = await container.GetBlobClient(blob.Name).DownloadContentAsync(cancellationToken);
        entries.Add(content.Value.Content.ToObjectFromJson<Entry>()!);
    }

    return new EntriesResponse(DateTimeOffset.UtcNow, entries.OrderByDescending(e => e.CreatedAt).ToList());
}).CacheOutput(p => p.Expire(TimeSpan.FromSeconds(30)).Tag(Entry.CacheTag));

// Adds an entry: writes it to Blob Storage as Pending, and queues its ID for
// the moderator.
app.MapPost("/entries", async (NewEntry request, BlobContainerClient container, QueueClient queue,
    IOutputCacheStore cache, CancellationToken cancellationToken) =>
{
    var name = request.Name?.Trim() ?? "";
    var message = request.Message?.Trim() ?? "";
    if (name.Length is 0 or > 50 || message.Length is 0 or > 500)
    {
        return Results.BadRequest("A name (up to 50 characters) and a message (up to 500) are required.");
    }

    var entry = new Entry(Guid.NewGuid(), name, message, DateTimeOffset.UtcNow, EntryState.Pending);
    await container.UploadBlobAsync(entry.BlobName, BinaryData.FromObjectAsJson(entry), cancellationToken);
    await queue.SendMessageAsync(entry.Id.ToString(), cancellationToken);
    await cache.EvictByTagAsync(Entry.CacheTag, cancellationToken);

    return Results.Created($"/entries/{entry.Id}", entry);
});

app.Run();
