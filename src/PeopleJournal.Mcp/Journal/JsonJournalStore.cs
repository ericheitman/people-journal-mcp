using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace PeopleJournal.Mcp.Journal;

/// <summary>
/// Stores the whole journal in one file, {RootPath}/{userId}/journal.json, using the same schema
/// as the companion journal web UI (meeting | learning | reflection entries, people by id).
/// The UI and Claude Code slash commands write the same file, so every save follows the shared
/// rule: check the last-modified time, refuse if it changed, back up the current file first.
/// </summary>
public sealed class JsonJournalStore(IOptions<JournalOptions> options, ILogger<JsonJournalStore> logger) : IJournalStore
{
    private const int MaxSaveAttempts = 3;

    private static readonly string[] Types = ["meeting", "learning", "reflection"];
    private static readonly string[] Tags = ["business", "org", "tech", "politics"];

    // Same options as the UI's API, so both writers produce identical formatting.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _root = Path.GetFullPath(options.Value.RootPath);
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public IReadOnlyList<string> SupportedTypes => Types;

    public async Task<JournalEntry> AddAsync(string userId, NewJournalEntry entry, CancellationToken ct = default)
    {
        var path = JournalPath(userId);

        await _writeLock.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                // Always reload before changing anything, so another writer's edits are kept.
                var (journal, loadedAt) = await LoadAsync(path, ct);
                var people = journal["people"]?.AsArray() ?? [];
                var entries = journal["entries"] as JsonArray ?? (JsonArray)(journal["entries"] = new JsonArray());

                var (personIds, unmatched) = ResolvePeople(people, entry.People);
                var tag = entry.Tags.FirstOrDefault(t => Tags.Contains(t, StringComparer.OrdinalIgnoreCase))?.ToLowerInvariant();
                var id = NextId(entries, entry.Date);

                var node = new JsonObject
                {
                    ["id"] = id,
                    ["date"] = entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["type"] = entry.Type,
                    ["people"] = new JsonArray([.. personIds.Select(p => JsonValue.Create(p))]),
                    ["text"] = ComposeText(entry, unmatched),
                };
                if (tag is not null) node["tag"] = tag;
                node["processed"] = false; // /process picks it up like a UI entry

                entries.Add(node);

                if (await TrySaveAsync(path, journal, loadedAt, ct))
                {
                    return ToEntry(node, people);
                }

                if (attempt == MaxSaveAttempts)
                {
                    throw new IOException("journal.json kept changing while saving. Try again in a moment.");
                }

                logger.LogInformation("journal.json changed during save (attempt {Attempt}); reloading and retrying.", attempt);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<JournalEntry>> SearchAsync(string userId, JournalQuery query, CancellationToken ct = default)
    {
        var path = JournalPath(userId);
        if (!File.Exists(path))
        {
            return [];
        }

        var (journal, _) = await LoadAsync(path, ct);
        var people = journal["people"]?.AsArray() ?? [];

        return [.. (journal["entries"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .Select(e => ToEntry(e, people))
            .Where(e => Matches(e, query))
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.Id, StringComparer.Ordinal)
            .Take(query.Limit)];
    }

    public async Task<JournalEntry?> GetAsync(string userId, string entryId, CancellationToken ct = default)
    {
        var path = JournalPath(userId);
        if (!File.Exists(path))
        {
            return null;
        }

        var (journal, _) = await LoadAsync(path, ct);
        var people = journal["people"]?.AsArray() ?? [];
        var node = (journal["entries"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .FirstOrDefault(e => string.Equals((string?)e["id"], entryId.Trim(), StringComparison.Ordinal));

        return node is null ? null : ToEntry(node, people);
    }

    // The UI's four free-text fields about a person, with the labels it shows.
    private static readonly (string Key, string Label)[] NoteFields =
    [
        ("caresAbout", "Cares about"),
        ("frustrations", "Frustrations"),
        ("howTheyLikeInfo", "How they like info"),
        ("successForMe", "What success looks like for me"),
    ];

    public async Task<IReadOnlyList<Person>> FindPeopleAsync(string userId, string? query, CancellationToken ct = default)
    {
        var path = JournalPath(userId);
        if (!File.Exists(path))
        {
            return [];
        }

        var (journal, _) = await LoadAsync(path, ct);
        var entries = (journal["entries"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        var commitments = (journal["commitments"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        var q = query?.Trim() ?? "";

        return [.. (journal["people"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .Where(p => q.Length == 0
                || new[] { (string?)p["id"], (string?)p["name"], (string?)p["title"] }
                    .Any(v => v?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))
            .Select(p => ToPerson(p, entries, commitments))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static Person ToPerson(JsonObject p, List<JsonObject> entries, List<JsonObject> commitments)
    {
        var id = (string?)p["id"] ?? "";
        var theirs = entries
            .Where(e => (e["people"]?.AsArray() ?? []).Any(x => (string?)x == id))
            .Select(e => DateOnly.TryParse((string?)e["date"], CultureInfo.InvariantCulture, out var d) ? d : (DateOnly?)null)
            .ToList();

        var notes = NoteFields
            .Select(f => (f.Label, Value: ((string?)p[f.Key])?.Trim() ?? ""))
            .Where(f => f.Value.Length > 0)
            .ToDictionary(f => f.Label, f => f.Value);

        var open = commitments
            .Where(c => (string?)c["person"] == id && c["done"]?.GetValue<bool>() != true)
            .Select(c => new Commitment(
                (string?)c["text"] ?? "",
                (string?)c["direction"] ?? "i-owe",
                DateOnly.TryParse((string?)c["due"], CultureInfo.InvariantCulture, out var due) ? due : null))
            .OrderBy(c => c.Due ?? DateOnly.MaxValue)
            .ToList();

        return new Person(
            (string?)p["name"] ?? id,
            theirs.Max(),
            theirs.Count,
            id,
            NullIfBlank((string?)p["title"]),
            NullIfBlank((string?)p["relationship"]),
            p["keyPerson"]?.GetValue<bool>() == true,
            notes,
            NullIfBlank((string?)p["linkedin"]),
            p["linkedinProfile"] is JsonObject li ? ToLinkedIn(li) : null,
            open);
    }

    private static LinkedInProfile ToLinkedIn(JsonObject li)
    {
        static string S(JsonNode? n) => ((string?)n)?.Trim() ?? "";
        static IEnumerable<JsonObject> Items(JsonNode? n) => (n as JsonArray ?? []).OfType<JsonObject>();

        var experience = Items(li["experience"])
            .Select(x =>
            {
                var role = string.Join(", ", new[] { S(x["title"]), S(x["company"]) }.Where(v => v.Length > 0));
                var (start, end) = (S(x["start"]), S(x["end"]));
                var dates = start.Length == 0 && end.Length == 0 ? "" : $" ({start}–{(end.Length == 0 ? "present" : end)})";
                return role + dates;
            })
            .Where(r => r.Length > 0)
            .ToList();

        var education = Items(li["education"])
            .Select(x => string.Join(", ", new[] { S(x["school"]), S(x["degree"]) }.Where(v => v.Length > 0)))
            .Where(e => e.Length > 0)
            .ToList();

        return new LinkedInProfile(
            S(li["headline"]),
            S(li["location"]),
            S(li["about"]),
            experience,
            education,
            [.. (li["skills"] as JsonArray ?? []).Select(S).Where(s => s.Length > 0)],
            DateOnly.TryParse(S(li["importedOn"]), CultureInfo.InvariantCulture, out var d) ? d : null);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string JournalPath(string userId)
    {
        var path = Path.GetFullPath(Path.Combine(_root, userId, "journal.json"));

        // Refuse any user id that escapes the root folder.
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Invalid user id.");
        }

        return path;
    }

    private static async Task<(JsonObject Journal, DateTime LoadedAt)> LoadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No journal.json for this user at {path}.");
        }

        var loadedAt = File.GetLastWriteTimeUtc(path);
        var journal = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject
            ?? throw new InvalidDataException("journal.json is not a JSON object.");
        return (journal, loadedAt);
    }

    private static async Task<bool> TrySaveAsync(string path, JsonObject journal, DateTime loadedAt, CancellationToken ct)
    {
        if (File.GetLastWriteTimeUtc(path) != loadedAt)
        {
            return false; // another writer saved since we loaded
        }

        var backup = Path.Combine(Path.GetDirectoryName(path)!, "journal.backup.json");
        File.Copy(path, backup, overwrite: true);

        // Write in place (not temp + rename) so a bind-mounted file keeps its identity and permissions.
        await File.WriteAllTextAsync(path, journal.ToJsonString(WriteOptions) + Environment.NewLine, ct);
        return true;
    }

    /// <summary>Matches names like "Dana", "Dana Whitfield (CFO)" or "CFO" to people ids. Ambiguous or unknown names are returned as unmatched.</summary>
    private static (List<string> Ids, List<string> Unmatched) ResolvePeople(JsonArray people, IReadOnlyList<string> names)
    {
        var ids = new List<string>();
        var unmatched = new List<string>();

        foreach (var name in names)
        {
            var candidates = people.OfType<JsonObject>()
                .Where(p => PersonMatches(p, name))
                .Select(p => (string?)p["id"])
                .OfType<string>()
                .Distinct()
                .ToList();

            if (candidates.Count == 1)
            {
                if (!ids.Contains(candidates[0])) ids.Add(candidates[0]);
            }
            else
            {
                unmatched.Add(name);
            }
        }

        return (ids, unmatched);
    }

    private static bool PersonMatches(JsonObject person, string name)
    {
        var id = (string?)person["id"] ?? "";
        var fullName = (string?)person["name"] ?? "";
        var title = (string?)person["title"] ?? "";

        // Strip a trailing "(role)" so "Dana Whitfield (CFO)" matches on the name part.
        var namePart = name.Split('(')[0].Trim();
        var role = name.Contains('(') ? name[(name.IndexOf('(') + 1)..].TrimEnd(')').Trim() : "";

        if (Eq(name, id) || Eq(namePart, fullName)) return true;
        if (namePart.Length > 0 && !namePart.Contains(' ') && Eq(namePart, fullName.Split(' ')[0])) return true;
        if (role.Length == 0 && Eq(name, title)) return true;
        return false;

        static bool Eq(string a, string b) => a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NextId(JsonArray entries, DateOnly date)
    {
        // Same scheme as the UI: e-yyyyMMdd-N, lowest unused N from 1.
        var compact = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var existing = entries.OfType<JsonObject>().Select(e => (string?)e["id"]).ToHashSet();
        var n = 1;
        while (existing.Contains($"e-{compact}-{n}")) n++;
        return $"e-{compact}-{n}";
    }

    /// <summary>The UI has no title field, so the title becomes the first line of the text. Nothing the caller sent is dropped.</summary>
    private static string ComposeText(NewJournalEntry entry, List<string> unmatchedPeople)
    {
        var body = entry.Body.Trim();
        var text = body.StartsWith(entry.Title, StringComparison.OrdinalIgnoreCase) ? body : $"{entry.Title}\n\n{body}";

        if (unmatchedPeople.Count > 0)
        {
            text += $"\n\nPeople not yet in the journal: {string.Join(", ", unmatchedPeople)}";
        }

        var extraTags = entry.Tags.Where(t => !Tags.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        if (extraTags.Count > 0)
        {
            text += $"\n\nTopics: {string.Join(", ", extraTags)}";
        }

        return text;
    }

    private static JournalEntry ToEntry(JsonObject e, JsonArray people)
    {
        var text = (string?)e["text"] ?? "";
        var firstLine = text.Split('\n', 2)[0].Trim();
        var title = firstLine.Length <= 80 ? firstLine : firstLine[..80].TrimEnd() + "…";

        var names = (e["people"]?.AsArray() ?? [])
            .Select(p => (string?)p)
            .OfType<string>()
            .Select(id => people.OfType<JsonObject>().FirstOrDefault(p => (string?)p["id"] == id) is { } person
                ? PersonLabel(person)
                : id)
            .ToList();

        var tag = (string?)e["tag"];

        return new JournalEntry(
            (string?)e["id"] ?? "",
            DateOnly.TryParse((string?)e["date"], CultureInfo.InvariantCulture, out var d) ? d : DateOnly.MinValue,
            (string?)e["type"] ?? "reflection",
            title.Length == 0 ? "(untitled)" : title,
            names,
            tag is null ? [] : [tag],
            text);
    }

    private static string PersonLabel(JsonObject person)
    {
        var name = (string?)person["name"] ?? (string?)person["id"] ?? "";
        var title = (string?)person["title"];
        return string.IsNullOrWhiteSpace(title) ? name : $"{name} ({title})";
    }

    private static bool Matches(JournalEntry e, JournalQuery q)
    {
        static bool Has(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        if (q.Since is { } since && e.Date < since) return false;
        if (!string.IsNullOrWhiteSpace(q.Type) && !string.Equals(e.Type, q.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(q.Tag) && !e.Tags.Any(t => string.Equals(t, q.Tag, StringComparison.OrdinalIgnoreCase))) return false;
        if (!string.IsNullOrWhiteSpace(q.Person) && !e.People.Any(p => Has(p, q.Person))) return false;

        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var terms = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var all = $"{e.Body} {string.Join(' ', e.People)} {string.Join(' ', e.Tags)}";
            if (!terms.All(t => Has(all, t))) return false;
        }

        return true;
    }
}
