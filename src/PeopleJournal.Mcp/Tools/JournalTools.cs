using System.ComponentModel;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using PeopleJournal.Mcp.Identity;
using PeopleJournal.Mcp.Journal;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace PeopleJournal.Mcp.Tools;

/// <summary>
/// Journal tools. The caller's identity comes from the HTTP request (via IHttpContextAccessor),
/// NOT from a tool parameter, so it isn't part of the input schema and the model can't
/// choose whose journal to use.
/// </summary>
[McpServerToolType]
public sealed class JournalTools(IJournalStore store, IUserContextResolver users, IHttpContextAccessor http)
{
    private ClaimsPrincipal? User => http.HttpContext?.User;

    [McpServerTool(Name = "journal_add_entry", Title = "Add journal entry", Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("""
        Save a new entry to the user's leadership journal. Use this when the user wants to log
        a 1:1, meeting notes, something they learned, a decision, or a reflection.
        Returns the saved entry's id.
        """)]
    public async Task<string> AddEntry(
        [Description("Usually meeting, learning or reflection. Some journals also accept 1on1, decision, teaching; an unknown type returns the allowed list.")] string type,
        [Description("Short title, e.g. 'Budget 1:1 with the CFO'.")] string title,
        [Description("The entry itself, in markdown. Keep the user's own words where possible.")] string body,
        [Description("People involved, as the user refers to them, e.g. ['Dana Whitfield (CFO)'].")] string[]? people = null,
        [Description("Optional topic tags, e.g. ['budget', 'integrations'].")] string[]? tags = null,
        [Description("Date of the event as yyyy-MM-dd. Defaults to today.")] string? date = null,
        CancellationToken ct = default)
    {
        var userId = users.ResolveUserId(User);
        var normalizedType = type.Trim().ToLowerInvariant();
        if (!store.SupportedTypes.Contains(normalizedType))
        {
            throw new McpException($"Unknown type '{type}'. Use one of: {string.Join(", ", store.SupportedTypes)}.");
        }

        var entry = await store.AddAsync(userId, new NewJournalEntry(
            ParseDate(date) ?? DateOnly.FromDateTime(DateTime.Now),
            normalizedType,
            title.Trim(),
            body,
            Clean(people),
            Clean(tags)), ct);

        return $"Saved \"{entry.Title}\" ({entry.Type}, {entry.Date:yyyy-MM-dd}). Entry id: {entry.Id}";
    }

    [McpServerTool(Name = "journal_search_entries", Title = "Search journal", ReadOnly = true, OpenWorld = false)]
    [Description("""
        Search the user's journal, newest first. All filters are optional and combine with AND.
        Use 'person' for questions like "what have I discussed with Dana?" (partial names and
        roles like 'CFO' work). Returns short previews; call journal_get_entry for full text.
        """)]
    public async Task<string> SearchEntries(
        [Description("Words that must all appear in the entry.")] string? text = null,
        [Description("Person name or role, partial match.")] string? person = null,
        [Description("Entry type, e.g. meeting, learning, reflection.")] string? type = null,
        [Description("Exact tag.")] string? tag = null,
        [Description("Only entries on or after this date, yyyy-MM-dd.")] string? since = null,
        [Description("Maximum results, 1-50. Default 10.")] int limit = 10,
        CancellationToken ct = default)
    {
        var userId = users.ResolveUserId(User);
        var results = await store.SearchAsync(userId,
            new JournalQuery(text, person, type, tag, ParseDate(since), Math.Clamp(limit, 1, 50)), ct);

        if (results.Count == 0)
        {
            return "No matching journal entries. Try fewer filters or a partial name.";
        }

        var sb = new StringBuilder($"Found {results.Count} entr{(results.Count == 1 ? "y" : "ies")}:\n");
        foreach (var e in results)
        {
            sb.AppendLine()
              .AppendLine($"- **{e.Title}** ({e.Type}, {e.Date:yyyy-MM-dd}), id: `{e.Id}`");
            if (e.People.Count > 0) sb.AppendLine($"  People: {string.Join(", ", e.People)}");
            sb.AppendLine($"  {Preview(e.Body)}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "journal_get_entry", Title = "Read journal entry", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Get the full text of one journal entry by the id returned from journal_search_entries or journal_add_entry.")]
    public async Task<string> GetEntry(
        [Description("Entry id exactly as returned by journal_search_entries or journal_add_entry.")] string id,
        CancellationToken ct = default)
    {
        var userId = users.ResolveUserId(User);
        var e = await store.GetAsync(userId, id, ct)
            ?? throw new McpException($"No entry with id '{id}'. Use journal_search_entries to find the right id.");

        var tagsLine = e.Tags.Count > 0 ? $"\nTags: {string.Join(", ", e.Tags)}" : "";
        var peopleLine = e.People.Count > 0 ? $"\nPeople: {string.Join(", ", e.People)}" : "";
        return $"# {e.Title}\n{e.Type}, {e.Date:yyyy-MM-dd}{peopleLine}{tagsLine}\n\n{e.Body}";
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new McpException($"Date '{value}' must be in yyyy-MM-dd format.");
    }

    private static string[] Clean(string[]? values) =>
        values?.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    private static string Preview(string body)
    {
        var flat = string.Join(' ', body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}
