using System.Globalization;
using System.Text.RegularExpressions;

namespace PrivateLedgerAgent.Ledger;

public sealed class ToolArgumentException(string message) : Exception(message);

public sealed class PeriodSpec
{
    /// <summary>all | last_n_months | last_n_days | calendar_year | calendar_month | year_to_date | custom</summary>
    public string Type { get; init; } = "all";
    public int? N { get; init; }
    public int? Year { get; init; }
    public string? Month { get; init; }      // yyyy-MM
    public string? StartDate { get; init; }  // yyyy-MM-dd
    public string? EndDate { get; init; }
}

public sealed class FilterSpec
{
    public string Direction { get; init; } = "spending"; // spending | income | all
    public List<string>? Categories { get; init; }
    public List<string>? DescriptionContains { get; init; }
    public string? Account { get; init; }
    public decimal? MinAmount { get; init; }
    public decimal? MaxAmount { get; init; }
    public PeriodSpec Period { get; init; } = new();
}

public sealed record ResolvedPeriod(DateOnly? Start, DateOnly? End, string Label, int? Months);

/// <summary>
/// All arithmetic lives here, in System.Decimal. The model can ask questions of this engine but never computes.
/// </summary>
public sealed partial class LedgerEngine
{
    private readonly IReadOnlyList<Transaction> _txns;
    private readonly HashSet<string> _excluded;
    private readonly string _currency;

    public LedgerEngine(IReadOnlyList<Transaction> txns, DateOnly asOf, IEnumerable<string> excludedFromSpendingAndIncome,
                        IReadOnlyList<string> files, string currencySymbol = "$")
    {
        _txns = txns;
        AsOf = asOf;
        Files = files;
        _excluded = new HashSet<string>(excludedFromSpendingAndIncome, StringComparer.OrdinalIgnoreCase);
        _currency = currencySymbol;
        Categories = txns.Select(t => t.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        ById = txns.ToDictionary(t => t.RefId, StringComparer.OrdinalIgnoreCase);
    }

    public DateOnly AsOf { get; }
    public IReadOnlyList<string> Files { get; }
    public IReadOnlyList<string> Categories { get; }
    public IReadOnlyDictionary<string, Transaction> ById { get; }
    public IReadOnlyList<Transaction> All => _txns;

    public string Money(decimal v) =>
        (v < 0 ? "-" : "") + _currency + Math.Abs(v).ToString("#,##0.00", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- tools

    public object Overview() => new
    {
        files = Files,
        transaction_count = _txns.Count,
        earliest_date = _txns.Count > 0 ? Iso(_txns[0].Date) : null,
        latest_date = _txns.Count > 0 ? Iso(_txns[^1].Date) : null,
        as_of_date = Iso(AsOf),
        accounts = _txns.Select(t => t.Account).Distinct().Order().ToList(),
        categories = Categories.Select(c => new
        {
            name = c,
            transaction_count = _txns.Count(t => t.Category.Equals(c, StringComparison.OrdinalIgnoreCase)),
            excluded_from_spending_and_income_by_default = _excluded.Contains(c),
        }).ToList(),
    };

    public (object Result, IReadOnlyList<Transaction> Evidence) Sum(FilterSpec f, int maxRefs)
    {
        var (rows, period, info) = Apply(f);
        var outflow = rows.Where(t => t.IsOutflow).Sum(t => -t.Amount);
        var inflow = rows.Where(t => t.IsInflow).Sum(t => t.Amount);
        var net = inflow - outflow;
        var (total, meaning) = Headline(f.Direction, outflow, inflow, net);

        var byCategory = rows.GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var o = g.Where(t => t.IsOutflow).Sum(t => -t.Amount);
                var i = g.Where(t => t.IsInflow).Sum(t => t.Amount);
                var (gt, _) = Headline(f.Direction, o, i, i - o);
                return new { category = g.Key, transaction_count = g.Count(), total = gt, total_formatted = Money(gt), share_of_total_pct = Pct(gt, total) };
            })
            .OrderByDescending(x => x.total).ToList();

        var result = new
        {
            period = PeriodJson(period),
            filters = info,
            transaction_count = rows.Count,
            total,
            total_formatted = Money(total),
            total_meaning = meaning,
            total_outflow = outflow,
            total_outflow_formatted = Money(outflow),
            total_inflow = inflow,
            total_inflow_formatted = Money(inflow),
            net,
            net_formatted = Money(net),
            months_in_period = period.Months,
            average_per_month = period.Months is > 0 ? Round(total / period.Months.Value) : (decimal?)null,
            average_per_month_formatted = period.Months is > 0 ? Money(Round(total / period.Months.Value)) : null,
            average_per_transaction = rows.Count > 0 ? Round(total / rows.Count) : (decimal?)null,
            average_per_transaction_formatted = rows.Count > 0 ? Money(Round(total / rows.Count)) : null,
            by_category = byCategory,
            coverage_warning = Coverage(period),
            ref_ids = rows.Take(maxRefs).Select(t => t.RefId).ToList(),
            ref_ids_total = rows.Count,
            ref_ids_truncated = rows.Count > maxRefs,
        };
        return (result, rows);
    }

    public (object Result, IReadOnlyList<Transaction> Evidence) Breakdown(FilterSpec f, string groupBy, int top)
    {
        var (rows, period, info) = Apply(f);
        Func<Transaction, string> key = groupBy switch
        {
            "category" => t => t.Category,
            "month" => t => t.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            "merchant" => t => MerchantKey(t.Description),
            "account" => t => t.Account,
            _ => throw new ToolArgumentException("group_by must be one of: category, month, merchant, account."),
        };

        var outAll = rows.Where(t => t.IsOutflow).Sum(t => -t.Amount);
        var inAll = rows.Where(t => t.IsInflow).Sum(t => t.Amount);
        var (grand, meaning) = Headline(f.Direction, outAll, inAll, inAll - outAll);

        var groups = rows.GroupBy(key, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var o = g.Where(t => t.IsOutflow).Sum(t => -t.Amount);
            var i = g.Where(t => t.IsInflow).Sum(t => t.Amount);
            var (gt, _) = Headline(f.Direction, o, i, i - o);
            return (Key: g.Key, Count: g.Count(), Total: gt);
        }).ToList();

        groups = groupBy == "month"
            ? groups.OrderBy(g => g.Key, StringComparer.Ordinal).ToList()
            : groups.OrderByDescending(g => g.Total).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();

        var shown = groups.Take(Math.Clamp(top, 1, 100)).ToList();
        var rest = groups.Skip(shown.Count).ToList();

        var result = new
        {
            period = PeriodJson(period),
            filters = info,
            group_by = groupBy,
            group_note = groupBy == "merchant" ? "merchant is derived deterministically from the description text" : null,
            grand_total = grand,
            grand_total_formatted = Money(grand),
            total_meaning = meaning,
            transaction_count = rows.Count,
            groups = shown.Select(g => new { key = g.Key, transaction_count = g.Count, total = g.Total, total_formatted = Money(g.Total), share_of_total_pct = Pct(g.Total, grand) }),
            other_groups = rest.Count == 0 ? null : new
            {
                group_count = rest.Count,
                transaction_count = rest.Sum(r => r.Count),
                total = rest.Sum(r => r.Total),
                total_formatted = Money(rest.Sum(r => r.Total)),
            },
            coverage_warning = Coverage(period),
        };
        return (result, rows);
    }

    public (object Result, IReadOnlyList<Transaction> Evidence) List(FilterSpec f, string sort, int limit, bool includeDetails)
    {
        var (rows, period, info) = Apply(f);
        IEnumerable<Transaction> sorted = sort switch
        {
            "date_asc" => rows.OrderBy(t => t.Date).ThenBy(t => t.RefId, StringComparer.Ordinal),
            "amount_desc" => rows.OrderByDescending(t => Math.Abs(t.Amount)).ThenBy(t => t.Date),
            "amount_asc" => rows.OrderBy(t => Math.Abs(t.Amount)).ThenBy(t => t.Date),
            _ => rows.OrderByDescending(t => t.Date).ThenBy(t => t.RefId, StringComparer.Ordinal),
        };
        var page = sorted.Take(limit).ToList();

        if (!includeDetails)
        {
            return (new
            {
                error = "Transaction-level details are not shared with the model under the current privacy settings. " +
                        "Tell the user they can type /refs to view the matching transactions locally.",
                period = PeriodJson(period),
                filters = info,
                transaction_count = rows.Count,
            }, rows);
        }

        return (new
        {
            period = PeriodJson(period),
            filters = info,
            sort,
            transaction_count = rows.Count,
            returned = page.Count,
            truncated = rows.Count > page.Count,
            transactions = page.Select(t => new
            {
                ref_id = t.RefId,
                date = Iso(t.Date),
                description = t.Description,
                amount = t.Amount,
                amount_formatted = Money(t.Amount),
                category = t.Category,
                account = t.Account,
            }),
            coverage_warning = Coverage(period),
        }, page);
    }

    public (object Result, IReadOnlyList<Transaction> Evidence) Compare(FilterSpec f, PeriodSpec a, PeriodSpec b, int maxRefs)
    {
        var fa = With(f, a);
        var fb = With(f, b);
        var (ra, pa, info) = Apply(fa);
        var (rb, pb, _) = Apply(fb);
        decimal Total(List<Transaction> rows)
        {
            var o = rows.Where(t => t.IsOutflow).Sum(t => -t.Amount);
            var i = rows.Where(t => t.IsInflow).Sum(t => t.Amount);
            return Headline(f.Direction, o, i, i - o).Total;
        }
        var ta = Total(ra);
        var tb = Total(rb);
        var diff = tb - ta;
        return (new
        {
            filters = info,
            total_meaning = Headline(f.Direction, 0, 0, 0).Meaning,
            period_a = new { period = PeriodJson(pa), transaction_count = ra.Count, total = ta, total_formatted = Money(ta), coverage_warning = Coverage(pa) },
            period_b = new { period = PeriodJson(pb), transaction_count = rb.Count, total = tb, total_formatted = Money(tb), coverage_warning = Coverage(pb) },
            difference_b_minus_a = diff,
            difference_b_minus_a_formatted = Money(diff),
            pct_change_from_a_to_b = ta != 0 ? Round(diff / ta * 100m, 1) : (decimal?)null,
            ref_ids_period_a = ra.Take(maxRefs / 2).Select(t => t.RefId),
            ref_ids_period_b = rb.Take(maxRefs / 2).Select(t => t.RefId),
        }, ra.Concat(rb).ToList());
    }

    // ---------------------------------------------------------------- core

    private (List<Transaction> Rows, ResolvedPeriod Period, object Info) Apply(FilterSpec f)
    {
        var direction = (f.Direction ?? "spending").ToLowerInvariant();
        if (direction is not ("spending" or "income" or "all"))
            throw new ToolArgumentException("direction must be one of: spending, income, all.");

        HashSet<string>? cats = null;
        if (f.Categories is { Count: > 0 })
        {
            var unknown = f.Categories.Where(c => !Categories.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0)
                throw new ToolArgumentException(
                    $"Unknown categor{(unknown.Count == 1 ? "y" : "ies")}: {string.Join(", ", unknown)}. " +
                    $"Valid categories are exactly: {string.Join(", ", Categories)}. Choose from these and call again.");
            cats = new HashSet<string>(f.Categories, StringComparer.OrdinalIgnoreCase);
        }

        var period = Resolve(f.Period);
        var excludedApplied = direction != "all"
            ? _excluded.Where(e => cats is null || !cats.Contains(e)).Order().ToList()
            : new List<string>();

        IEnumerable<Transaction> q = _txns;
        if (period.Start is { } s) q = q.Where(t => t.Date >= s);
        if (period.End is { } e) q = q.Where(t => t.Date <= e);
        if (direction == "spending") q = q.Where(t => t.IsOutflow);
        if (direction == "income") q = q.Where(t => t.IsInflow);
        if (excludedApplied.Count > 0) q = q.Where(t => !excludedApplied.Contains(t.Category, StringComparer.OrdinalIgnoreCase));
        if (cats is not null) q = q.Where(t => cats.Contains(t.Category));
        if (f.DescriptionContains is { Count: > 0 } terms)
            q = q.Where(t => terms.Any(term => t.Description.Contains(term, StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(f.Account))
            q = q.Where(t => t.Account.Contains(f.Account, StringComparison.OrdinalIgnoreCase));
        if (f.MinAmount is { } min) q = q.Where(t => Math.Abs(t.Amount) >= min);
        if (f.MaxAmount is { } max) q = q.Where(t => Math.Abs(t.Amount) <= max);

        var rows = q.ToList();
        var info = new
        {
            direction,
            categories_included = cats is null
                ? "all categories" + (excludedApplied.Count > 0 ? " except excluded" : "")
                : string.Join(", ", Categories.Where(c => cats.Contains(c))),
            excluded_categories = excludedApplied.Count > 0 ? excludedApplied : null,
            description_contains = f.DescriptionContains,
            account = f.Account,
            min_amount = f.MinAmount,
            max_amount = f.MaxAmount,
        };
        return (rows, period, info);
    }

    public ResolvedPeriod Resolve(PeriodSpec p)
    {
        switch ((p.Type ?? "all").ToLowerInvariant())
        {
            case "all":
            {
                if (_txns.Count == 0) return new ResolvedPeriod(null, null, "all available data", null);
                var s = _txns[0].Date; var e = _txns[^1].Date;
                return new ResolvedPeriod(s, e, "all available data", MonthsSpanned(s, e));
            }
            case "last_n_months":
            {
                var n = p.N ?? throw new ToolArgumentException("last_n_months requires n.");
                if (n is < 1 or > 600) throw new ToolArgumentException("n must be between 1 and 600.");
                return new ResolvedPeriod(AsOf.AddMonths(-n).AddDays(1), AsOf, $"last {n} month{(n == 1 ? "" : "s")} ending {Iso(AsOf)}", n);
            }
            case "last_n_days":
            {
                var n = p.N ?? throw new ToolArgumentException("last_n_days requires n.");
                if (n is < 1 or > 20000) throw new ToolArgumentException("n must be between 1 and 20000.");
                return new ResolvedPeriod(AsOf.AddDays(-n + 1), AsOf, $"last {n} day{(n == 1 ? "" : "s")} ending {Iso(AsOf)}", null);
            }
            case "calendar_year":
            {
                var y = p.Year ?? throw new ToolArgumentException("calendar_year requires year.");
                var end = new DateOnly(y, 12, 31);
                var months = y == AsOf.Year ? AsOf.Month : 12;
                return new ResolvedPeriod(new DateOnly(y, 1, 1), end, $"calendar year {y}", months);
            }
            case "calendar_month":
            {
                if (p.Month is null || !DateOnly.TryParseExact(p.Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var m))
                    throw new ToolArgumentException("calendar_month requires month as yyyy-MM.");
                return new ResolvedPeriod(m, m.AddMonths(1).AddDays(-1), $"calendar month {p.Month}", 1);
            }
            case "year_to_date":
                return new ResolvedPeriod(new DateOnly(AsOf.Year, 1, 1), AsOf, $"year to date ({AsOf.Year}) ending {Iso(AsOf)}", AsOf.Month);
            case "custom":
            {
                var s = ParseDate(p.StartDate, "start_date");
                var e = ParseDate(p.EndDate, "end_date");
                if (s is null && e is null) throw new ToolArgumentException("custom requires start_date and/or end_date (yyyy-MM-dd).");
                if (s > e) throw new ToolArgumentException("start_date is after end_date.");
                var label = $"{(s is null ? "beginning of data" : Iso(s.Value))} to {(e is null ? "end of data" : Iso(e.Value))}";
                int? months = s is not null && e is not null && s.Value.Day == 1 && e.Value.AddDays(1).Day == 1
                    ? MonthsSpanned(s.Value, e.Value) : null;
                return new ResolvedPeriod(s, e, label, months);
            }
            default:
                throw new ToolArgumentException("period.type must be one of: all, last_n_months, last_n_days, calendar_year, calendar_month, year_to_date, custom.");
        }
    }

    private string? Coverage(ResolvedPeriod p)
    {
        if (_txns.Count == 0) return "The ledger contains no transactions.";
        var first = _txns[0].Date; var last = _txns[^1].Date;
        var notes = new List<string>();
        if (p.Start is { } s && s < first) notes.Add($"the requested period starts {Iso(s)} but the data begins {Iso(first)}");
        if (p.End is { } e && e > last) notes.Add($"the requested period ends {Iso(e)} but the latest transaction is {Iso(last)}");
        return notes.Count == 0 ? null : "Partial coverage: " + string.Join("; ", notes) + ".";
    }

    private static FilterSpec With(FilterSpec f, PeriodSpec p) => new()
    {
        Direction = f.Direction, Categories = f.Categories, DescriptionContains = f.DescriptionContains,
        Account = f.Account, MinAmount = f.MinAmount, MaxAmount = f.MaxAmount, Period = p,
    };

    private static (decimal Total, string Meaning) Headline(string direction, decimal outflow, decimal inflow, decimal net) =>
        direction.ToLowerInvariant() switch
        {
            "income" => (inflow, "total money in (income)"),
            "all" => (net, "net change (money in minus money out)"),
            _ => (outflow, "total money spent (outflows, shown as a positive amount)"),
        };

    private static decimal? Pct(decimal part, decimal whole) => whole != 0 ? Round(part / whole * 100m, 1) : null;
    private static decimal Round(decimal v, int places = 2) => Math.Round(v, places, MidpointRounding.AwayFromZero);
    private static int MonthsSpanned(DateOnly s, DateOnly e) => (e.Year - s.Year) * 12 + e.Month - s.Month + 1;
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static object PeriodJson(ResolvedPeriod p) => new { label = p.Label, start = p.Start is { } s ? Iso(s) : null, end = p.End is { } e ? Iso(e) : null };

    private static DateOnly? ParseDate(string? s, string name)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new ToolArgumentException($"{name} must be yyyy-MM-dd.");
    }

    public static string MerchantKey(string description)
    {
        var s = MerchantNoise().Replace(description.ToUpperInvariant(), " ");
        s = Spaces().Replace(s, " ").Trim();
        return s.Length == 0 ? description.Trim() : s;
    }

    [GeneratedRegex(@"(#\s*\d+|\b\d[\d\-/*]*\b|\*+)")] private static partial Regex MerchantNoise();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}
