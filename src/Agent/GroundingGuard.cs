using System.Globalization;
using System.Text.RegularExpressions;

namespace PrivateLedgerAgent.Agent;

public sealed record GuardResult(bool Ok, IReadOnlyList<string> UnverifiedNumbers, IReadOnlyList<string> UnknownRefs)
{
    public string Describe() => string.Join(" ", new[]
    {
        UnverifiedNumbers.Count > 0 ? $"Numbers not found in any tool result: {string.Join(", ", UnverifiedNumbers)}." : null,
        UnknownRefs.Count > 0 ? $"Reference ids not returned by any tool: {string.Join(", ", UnknownRefs)}." : null,
    }.Where(s => s is not null));
}

/// <summary>
/// Post-generation check: every number and every reference id in the model's answer must appear verbatim
/// (by value) in a tool result or in the user's own question. Anything else is treated as a hallucination.
/// </summary>
public static partial class GroundingGuard
{
    public static GuardResult Verify(string answer, IEnumerable<string> groundTexts)
    {
        var allowedNumbers = new HashSet<decimal>();
        var allowedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in groundTexts)
        {
            foreach (Match m in RefPattern().Matches(text)) allowedRefs.Add(m.Value);
            foreach (var n in Numbers(text)) allowedNumbers.Add(n.Value);
        }

        var unknownRefs = RefPattern().Matches(answer).Select(m => m.Value)
            .Where(r => !allowedRefs.Contains(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var scrubbed = RefPattern().Replace(answer, " ");
        scrubbed = ListMarker().Replace(scrubbed, "$1"); // "1. " / "2) " list numbering is not data

        var unverified = Numbers(scrubbed).Where(n => !allowedNumbers.Contains(n.Value))
            .Select(n => n.Raw).Distinct().ToList();

        return new GuardResult(unverified.Count == 0 && unknownRefs.Count == 0, unverified, unknownRefs);
    }

    private static IEnumerable<(string Raw, decimal Value)> Numbers(string text)
    {
        foreach (Match m in NumberPattern().Matches(text))
        {
            var raw = m.Value.TrimEnd(',');
            if (decimal.TryParse(raw.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v))
                yield return (raw, v);
        }
    }

    [GeneratedRegex(@"[\w.\-]+\.csv#L\d+", RegexOptions.IgnoreCase)] private static partial Regex RefPattern();
    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")] private static partial Regex NumberPattern();
    [GeneratedRegex(@"(^|\n)[ \t]*\d{1,2}[.)][ \t]+")] private static partial Regex ListMarker();
}
