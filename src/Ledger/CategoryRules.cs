using System.Text.Json;

namespace PrivateLedgerAgent.Ledger;

/// <summary>
/// Deterministic categorization. The model never assigns categories — that would make totals vary run to run.
/// </summary>
public sealed class CategoryRules
{
    public bool OverrideFileCategories { get; set; }
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CategoryRule> Rules { get; set; } = new();

    public static CategoryRules Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new CategoryRules();
        var rules = JsonSerializer.Deserialize<CategoryRules>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) ?? new CategoryRules();
        rules.Aliases = new Dictionary<string, string>(rules.Aliases, StringComparer.OrdinalIgnoreCase);
        return rules;
    }

    public (string Category, string Source) Categorize(string description, string? fileCategory)
    {
        var fromFile = string.IsNullOrWhiteSpace(fileCategory) ? null : fileCategory.Trim();
        if (fromFile is not null && Aliases.TryGetValue(fromFile, out var alias)) fromFile = alias;

        if (fromFile is not null && !OverrideFileCategories) return (fromFile, "file");

        foreach (var rule in Rules)
        {
            foreach (var needle in rule.Contains)
            {
                if (description.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return (rule.Category, $"rule:\"{needle}\"");
            }
        }

        return fromFile is not null ? (fromFile, "file") : ("Uncategorized", "none");
    }
}

public sealed class CategoryRule
{
    public string Category { get; set; } = "";
    public List<string> Contains { get; set; } = new();
}
