using System.Globalization;
using System.Text.RegularExpressions;
using PrivateLedgerAgent.Config;

namespace PrivateLedgerAgent.Ledger;

public sealed class LoadResult
{
    public List<Transaction> Transactions { get; } = new();
    public List<string> Files { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>Reads bank CSV exports from disk. Nothing here touches the network.</summary>
public static partial class LedgerLoader
{
    public static LoadResult Load(IEnumerable<string> paths, DataConfig cfg, CategoryRules rules, bool maskAccounts)
    {
        var result = new LoadResult();
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) files.AddRange(Directory.GetFiles(p, "*.csv").OrderBy(f => f, StringComparer.Ordinal));
            else if (File.Exists(p)) files.Add(p);
            else result.Warnings.Add($"Path not found: {p}");
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Distinct())
        {
            var name = Path.GetFileName(file);
            if (!seenNames.Add(name))
            {
                result.Warnings.Add($"Skipped {file}: another file named {name} is already loaded (reference ids must be unique).");
                continue;
            }
            result.Files.Add(name);
            LoadFile(file, name, cfg, rules, maskAccounts, result);
        }

        result.Transactions.Sort((a, b) =>
        {
            var c = a.Date.CompareTo(b.Date);
            return c != 0 ? c : string.CompareOrdinal(a.RefId, b.RefId);
        });
        return result;
    }

    private static void LoadFile(string path, string name, DataConfig cfg, CategoryRules rules, bool mask, LoadResult result)
    {
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        string[]? header = null;
        int iDate = -1, iDesc = -1, iAmt = -1, iDebit = -1, iCredit = -1, iCat = -1, iAcct = -1;
        var defaultAccount = Path.GetFileNameWithoutExtension(name);
        var outflowPositive = cfg.AmountSign.Equals("PositiveIsOutflow", StringComparison.OrdinalIgnoreCase);

        foreach (var (line, f) in CsvParser.Parse(reader))
        {
            if (header is null)
            {
                header = f.Select(h => h.Trim()).ToArray();
                iDate = Find(header, cfg.Columns.Date);
                iDesc = Find(header, cfg.Columns.Description);
                iAmt = Find(header, cfg.Columns.Amount);
                iDebit = Find(header, cfg.Columns.Debit);
                iCredit = Find(header, cfg.Columns.Credit);
                iCat = Find(header, cfg.Columns.Category);
                iAcct = Find(header, cfg.Columns.Account);
                if (iDate < 0 || iDesc < 0 || (iAmt < 0 && iDebit < 0 && iCredit < 0))
                {
                    result.Warnings.Add($"{name}: header not recognised ({string.Join(", ", header)}). " +
                                        "Add your column names to Data.Columns in appsettings.json.");
                    return;
                }
                continue;
            }

            if (f.All(string.IsNullOrWhiteSpace)) continue;
            var refId = $"{name}#L{line}";

            var dateText = At(f, iDate);
            if (!DateOnly.TryParseExact(dateText.Trim(), cfg.DateFormats.ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                result.Warnings.Add($"{refId}: unreadable date '{dateText}' — row skipped.");
                continue;
            }

            decimal amount;
            if (iAmt >= 0 && TryParseMoney(At(f, iAmt), out var a))
            {
                amount = outflowPositive ? -a : a;
            }
            else
            {
                var hasDebit = TryParseMoney(At(f, iDebit), out var d) && d != 0;
                var hasCredit = TryParseMoney(At(f, iCredit), out var cr) && cr != 0;
                if (!hasDebit && !hasCredit)
                {
                    result.Warnings.Add($"{refId}: no readable amount — row skipped.");
                    continue;
                }
                amount = (hasCredit ? Math.Abs(cr) : 0m) - (hasDebit ? Math.Abs(d) : 0m);
            }

            var description = Collapse(At(f, iDesc));
            var (category, source) = rules.Categorize(description, iCat >= 0 ? At(f, iCat) : null);
            var account = iAcct >= 0 && At(f, iAcct).Trim() is { Length: > 0 } acct ? acct : defaultAccount;
            if (mask) account = MaskAccount(account);

            result.Transactions.Add(new Transaction(refId, name, line, date, description, amount, category, source, account));
        }
    }

    public static bool TryParseMoney(string? s, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.EndsWith('-')) { negative = true; t = t[..^1]; }
        t = t.Replace("$", "").Replace("€", "").Replace("£", "").Replace(",", "").Replace(" ", "").Trim();
        if (t.StartsWith('+')) t = t[1..];
        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        if (negative) value = -Math.Abs(value);
        return true;
    }

    /// <summary>Keeps only the last 4 digits of anything that looks like an account/card number.</summary>
    public static string MaskAccount(string account) =>
        LongDigits().Replace(account, m => "****" + m.Value[^4..]);

    private static int Find(string[] header, List<string> candidates)
    {
        foreach (var c in candidates)
        {
            var i = Array.FindIndex(header, h => h.Equals(c, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static string At(string[] f, int i) => i >= 0 && i < f.Length ? f[i] : "";
    private static string Collapse(string s) => Whitespace().Replace(s.Trim(), " ");

    [GeneratedRegex(@"\d{4,}")] private static partial Regex LongDigits();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
}
