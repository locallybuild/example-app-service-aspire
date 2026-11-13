using System.Text.Json.Serialization;

namespace Api;

[JsonConverter(typeof(JsonStringEnumConverter<EntryState>))]
public enum EntryState
{
    Pending,
    Approved,
    Rejected,
}

public record Entry(Guid Id, string Name, string Message, DateTimeOffset CreatedAt, EntryState State,
    DateTimeOffset? ModeratedAt = null)
{
    // The output cache tag on the list of entries, evicted whenever one changes.
    public const string CacheTag = "entries";

    public string BlobName => $"{Id}.json";
}

public record NewEntry(string? Name, string? Message);

// CachedAt shows when the api built the response, so you can see the output
// cache serving the same one until it expires or an entry changes.
public record EntriesResponse(DateTimeOffset CachedAt, IReadOnlyList<Entry> Entries);
