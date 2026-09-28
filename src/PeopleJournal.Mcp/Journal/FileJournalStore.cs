using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PeopleJournal.Mcp.Journal;

/// <summary>
/// Stores each entry as a markdown file with YAML frontmatter:
/// {RootPath}/{userId}/entries/yyyy/MM/yyyy-MM-dd-title-slug.md
/// The files stay readable, greppable and git-friendly, so Claude Code slash commands
/// can keep working on the same journal.
/// </summary>
public sealed partial class FileJournalStore(IOptions<JournalOptions> options) : IJournalStore
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    private static readonly ISerializer Yaml = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer YamlReader = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public IReadOnlyList<string> SupportedTypes => EntryTypes.All;

    public async Task<JournalEntry> AddAsync(string userId, NewJournalEntry entry, CancellationToken ct = default)
    {
        var folder = Path.Combine(EntriesRoot(userId), entry.Date.ToString("yyyy"), entry.Date.ToString("MM"));
        Directory.CreateDirectory(folder);

        var baseName = $"{entry.Date:yyyy-MM-dd}-{Slug(entry.Title)}";
        var path = Path.Combine(folder, baseName + ".md");
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{baseName}-{n}.md");
        }

        var frontmatter = new Frontmatter
        {
            Date = entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Type = entry.Type,
            Title = entry.Title,
            People = [.. entry.People],
            Tags = [.. entry.Tags],
        };

        var content = new StringBuilder()
            .AppendLine("---")
            .Append(Yaml.Serialize(frontmatter))
            .AppendLine("---")
            .AppendLine()
            .AppendLine(entry.Body.Trim())
            .ToString();

        await File.WriteAllTextAsync(path, content, ct);
        return (await ReadAsync(EntriesRoot(userId), path, ct))!;
    }

    public async Task<IReadOnlyList<JournalEntry>> SearchAsync(string userId, JournalQuery query, CancellationToken ct = default)
    {
        var root = EntriesRoot(userId);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<JournalEntry>();
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var entry = await ReadAsync(root, file, ct);
            if (entry is not null && Matches(entry, query))
            {
                results.Add(entry);
            }
        }

        return [.. results.OrderByDescending(e => e.Date).ThenByDescending(e => e.Id).Take(query.Limit)];
    }

    public async Task<JournalEntry?> GetAsync(string userId, string entryId, CancellationToken ct = default)
    {
        var root = EntriesRoot(userId);
        var path = Path.GetFullPath(Path.Combine(root, entryId.Trim('/') + ".md"));

        // Refuse anything that escapes this user's folder (e.g. "../other-user/...").
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }

        return await ReadAsync(root, path, ct);
    }

    /// <summary>Markdown entries only name people, so a person here is a distinct name from entries' frontmatter.</summary>
    public async Task<IReadOnlyList<Person>> FindPeopleAsync(string userId, string? query, CancellationToken ct = default)
    {
        var entries = await SearchAsync(userId, new JournalQuery(Person: query?.Trim(), Limit: int.MaxValue), ct);

        return [.. entries
            .SelectMany(e => e.People.Select(name => (Name: name, e.Date)))
            .Where(x => string.IsNullOrWhiteSpace(query) || x.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Person(g.First().Name, g.Max(x => x.Date), g.Count()))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private string EntriesRoot(string userId) => Path.Combine(_root, userId, "entries");

    private static bool Matches(JournalEntry e, JournalQuery q)
    {
        static bool Has(string haystack, string needle) =>
            haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

        if (q.Since is { } since && e.Date < since) return false;
        if (!string.IsNullOrWhiteSpace(q.Type) && !string.Equals(e.Type, q.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(q.Tag) && !e.Tags.Any(t => string.Equals(t, q.Tag, StringComparison.OrdinalIgnoreCase))) return false;

        // Forgiving person match: "Dana" finds "Dana Whitfield (CFO)", and so does "CFO".
        if (!string.IsNullOrWhiteSpace(q.Person) && !e.People.Any(p => Has(p, q.Person))) return false;

        if (!string.IsNullOrWhiteSpace(q.Text))
        {
            var terms = q.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var all = $"{e.Title} {e.Body} {string.Join(' ', e.People)} {string.Join(' ', e.Tags)}";
            if (!terms.All(t => Has(all, t))) return false;
        }

        return true;
    }

    private static async Task<JournalEntry?> ReadAsync(string root, string path, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(path, ct);
        var id = Path.ChangeExtension(Path.GetRelativePath(root, path), null).Replace('\\', '/');

        var match = FrontmatterBlock().Match(text);
        if (!match.Success)
        {
            return null; // not a journal entry; skip it
        }

        var fm = YamlReader.Deserialize<Frontmatter>(match.Groups["yaml"].Value) ?? new Frontmatter();
        var date = DateOnly.TryParse(fm.Date, CultureInfo.InvariantCulture, out var d)
            ? d
            : DateOnly.FromDateTime(File.GetLastWriteTimeUtc(path));

        return new JournalEntry(
            id,
            date,
            fm.Type ?? "reflection",
            fm.Title ?? Path.GetFileNameWithoutExtension(path),
            fm.People ?? [],
            fm.Tags ?? [],
            text[match.Length..].Trim());
    }

    private static string Slug(string title)
    {
        var slug = NonSlugChars().Replace(title.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "entry" : slug[..Math.Min(slug.Length, 60)].TrimEnd('-');
    }

    [GeneratedRegex(@"\A---\r?\n(?<yaml>.*?)\r?\n---\r?\n", RegexOptions.Singleline)]
    private static partial Regex FrontmatterBlock();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    private sealed class Frontmatter
    {
        public string? Date { get; set; }
        public string? Type { get; set; }
        public string? Title { get; set; }
        public List<string>? People { get; set; }
        public List<string>? Tags { get; set; }
    }
}
