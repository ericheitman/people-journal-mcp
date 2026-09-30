namespace PeopleJournal.Mcp.Journal;

public sealed class JournalOptions
{
    /// <summary>Folder holding one sub-folder per user: {RootPath}/{userId}/...</summary>
    public string RootPath { get; set; } = "/data/journal";

    /// <summary>
    /// Markdown: one file per entry under {userId}/entries/ (FileJournalStore).
    /// Json: the web UI's single {userId}/journal.json (JsonJournalStore).
    /// </summary>
    public JournalStoreKind Store { get; set; } = JournalStoreKind.Markdown;
}

public enum JournalStoreKind { Markdown, Json }

public static class EntryTypes
{
    public static readonly string[] All = ["1on1", "meeting", "learning", "decision", "reflection", "teaching"];
}

/// <summary>A journal entry. Id is its path under entries/ without ".md", e.g. "2026/09/2026-09-24-cfo-1on1".</summary>
public sealed record JournalEntry(
    string Id,
    DateOnly Date,
    string Type,
    string Title,
    IReadOnlyList<string> People,
    IReadOnlyList<string> Tags,
    string Body);

public sealed record NewJournalEntry(
    DateOnly Date,
    string Type,
    string Title,
    string Body,
    IReadOnlyList<string> People,
    IReadOnlyList<string> Tags);

public sealed record JournalQuery(
    string? Text = null,
    string? Person = null,
    string? Type = null,
    string? Tag = null,
    DateOnly? Since = null,
    int Limit = 10);

/// <summary>
/// A person in the journal. The markdown store only knows names from entries, so everything
/// beyond Name, LastEntry and EntryCount is optional and filled only by stores that keep it.
/// </summary>
public sealed record Person(
    string Name,
    DateOnly? LastEntry,
    int EntryCount,
    string? Id = null,
    string? Title = null,
    string? Relationship = null,
    bool KeyPerson = false,
    IReadOnlyDictionary<string, string>? Notes = null,
    string? LinkedInUrl = null,
    LinkedInProfile? LinkedIn = null,
    IReadOnlyList<Commitment>? OpenCommitments = null,
    string? Phone = null,
    string? Email = null);

/// <summary>Facts the user pasted from a LinkedIn profile (never fetched). Roles are "Title, Company (start–end)".</summary>
public sealed record LinkedInProfile(
    string Headline,
    string Location,
    string About,
    IReadOnlyList<string> Experience,
    IReadOnlyList<string> Education,
    IReadOnlyList<string> Skills,
    DateOnly? ImportedOn);

/// <summary>Direction is "i-owe" or "owed-to-me".</summary>
public sealed record Commitment(string Text, string Direction, DateOnly? Due);

public interface IJournalStore
{
    /// <summary>Entry types this store accepts.</summary>
    IReadOnlyList<string> SupportedTypes { get; }

    /// <summary>People whose name, id or title contains <paramref name="query"/> (all people when null), by name.</summary>
    Task<IReadOnlyList<Person>> FindPeopleAsync(string userId, string? query, CancellationToken ct = default);

    Task<JournalEntry> AddAsync(string userId, NewJournalEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<JournalEntry>> SearchAsync(string userId, JournalQuery query, CancellationToken ct = default);
    Task<JournalEntry?> GetAsync(string userId, string entryId, CancellationToken ct = default);
}
