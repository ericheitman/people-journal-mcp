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

public interface IJournalStore
{
    /// <summary>Entry types this store accepts.</summary>
    IReadOnlyList<string> SupportedTypes { get; }

    Task<JournalEntry> AddAsync(string userId, NewJournalEntry entry, CancellationToken ct = default);
    Task<IReadOnlyList<JournalEntry>> SearchAsync(string userId, JournalQuery query, CancellationToken ct = default);
    Task<JournalEntry?> GetAsync(string userId, string entryId, CancellationToken ct = default);
}
