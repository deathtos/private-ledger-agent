using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateLedgerAgent.Config;
using PrivateLedgerAgent.Ledger;

namespace PrivateLedgerAgent.Agent;

public sealed record ToolExecution(string Name, string ArgumentsJson, string ResultJson, IReadOnlyList<Transaction> Evidence, bool IsError);

/// <summary>The only window the model has onto the ledger.</summary>
public sealed class LedgerTools(LedgerEngine engine, PrivacyConfig privacy)
{
    private static readonly JsonSerializerOptions Out = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private const string PeriodSchema = """
    {
      "type": "object",
      "description": "Date range. Relative periods are resolved by the app against the as-of date; never compute dates yourself when a relative type fits.",
      "properties": {
        "type": { "type": "string", "enum": ["all","last_n_months","last_n_days","calendar_year","calendar_month","year_to_date","custom"] },
        "n": { "type": "integer", "description": "For last_n_months / last_n_days." },
        "year": { "type": "integer", "description": "For calendar_year." },
        "month": { "type": "string", "description": "For calendar_month, yyyy-MM." },
        "start_date": { "type": "string", "description": "For custom, yyyy-MM-dd (inclusive)." },
        "end_date": { "type": "string", "description": "For custom, yyyy-MM-dd (inclusive)." }
      },
      "required": ["type"]
    }
    """;

    private static string FilterProps(bool includePeriod) => $$"""
        "direction": { "type": "string", "enum": ["spending","income","all"], "description": "spending = money out, income = money in, all = both (net). Transfers/card payments are excluded from spending and income unless named in categories." },
        "categories": { "type": "array", "items": { "type": "string" }, "description": "Exact category names from get_dataset_overview. Omit for all categories." },
        "description_contains": { "type": "array", "items": { "type": "string" }, "description": "Match transactions whose description contains ANY of these terms (case-insensitive), e.g. a merchant name." },
        "account": { "type": "string", "description": "Masked account label (or part of it) from get_dataset_overview." },
        "min_amount": { "type": "number", "description": "Minimum absolute transaction amount." },
        "max_amount": { "type": "number", "description": "Maximum absolute transaction amount." }
        {{(includePeriod ? $", \"period\": {PeriodSchema}" : "")}}
    """;

    public JsonArray Definitions { get; } = BuildDefinitions(privacy);

    private static JsonArray BuildDefinitions(PrivacyConfig privacy)
    {
        var tools = new List<string>
        {
            Fn("get_dataset_overview",
               "Lists source files, date coverage, the as-of date, masked accounts and the exact category names. Call this first to map the user's words (e.g. 'food') onto real categories.",
               """{ "type": "object", "properties": {} }"""),
            Fn("sum_transactions",
               "Exact totals for a filtered set of transactions: total, outflow, inflow, net, count, per-month and per-transaction averages, per-category subtotals with percentage shares, and reference ids.",
               $$"""{ "type": "object", "properties": { {{FilterProps(true)}} }, "required": ["direction","period"] }"""),
            Fn("breakdown",
               "Exact subtotals grouped by category, month, merchant or account, with percentage shares, sorted largest first (months chronologically).",
               $$"""{ "type": "object", "properties": { "group_by": { "type": "string", "enum": ["category","month","merchant","account"] }, "top": { "type": "integer", "description": "Max groups to return (default 20)." }, {{FilterProps(true)}} }, "required": ["group_by","direction","period"] }"""),
            Fn("compare_periods",
               "Exact totals for the same filter over two periods, with the difference (b minus a) and percent change.",
               $$"""{ "type": "object", "properties": { {{FilterProps(false)}}, "period_a": {{PeriodSchema}}, "period_b": {{PeriodSchema}} }, "required": ["direction","period_a","period_b"] }"""),
        };
        if (privacy.ShareTransactionDetailsWithModel)
        {
            tools.Add(Fn("list_transactions",
               $"Individual transactions (ref id, date, description, signed amount, category, account). At most {privacy.MaxRowsToModel} rows.",
               $$"""{ "type": "object", "properties": { "sort": { "type": "string", "enum": ["date_desc","date_asc","amount_desc","amount_asc"] }, "limit": { "type": "integer" }, {{FilterProps(true)}} }, "required": ["direction","period"] }"""));
        }
        return new JsonArray(tools.Select(t => JsonNode.Parse(t)).ToArray());
    }

    private static string Fn(string name, string description, string parameters) =>
        $$"""{ "type": "function", "function": { "name": "{{name}}", "description": {{JsonSerializer.Serialize(description)}}, "parameters": {{parameters}} } }""";

    public ToolExecution Execute(ToolCall call)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var a = doc.RootElement;
            (object result, IReadOnlyList<Transaction> evidence) = call.Name switch
            {
                "get_dataset_overview" => (engine.Overview(), Array.Empty<Transaction>()),
                "sum_transactions" => engine.Sum(Filter(a, a.TryGetProperty("period", out var p) ? Period(p) : new PeriodSpec()), privacy.MaxRefIdsToModel),
                "breakdown" => engine.Breakdown(Filter(a, a.TryGetProperty("period", out var p) ? Period(p) : new PeriodSpec()),
                                                Str(a, "group_by") ?? "category", Int(a, "top") ?? 20),
                "compare_periods" => engine.Compare(Filter(a, new PeriodSpec()),
                                                    Period(Required(a, "period_a")), Period(Required(a, "period_b")), privacy.MaxRefIdsToModel),
                "list_transactions" => engine.List(Filter(a, a.TryGetProperty("period", out var p) ? Period(p) : new PeriodSpec()),
                                                   Str(a, "sort") ?? "date_desc",
                                                   Math.Clamp(Int(a, "limit") ?? 20, 1, privacy.MaxRowsToModel),
                                                   privacy.ShareTransactionDetailsWithModel),
                _ => throw new ToolArgumentException($"Unknown tool '{call.Name}'."),
            };
            return new ToolExecution(call.Name, call.ArgumentsJson, JsonSerializer.Serialize(result, Out), evidence, false);
        }
        catch (Exception ex) when (ex is ToolArgumentException or JsonException or InvalidOperationException or FormatException)
        {
            var err = JsonSerializer.Serialize(new { error = ex.Message });
            return new ToolExecution(call.Name, call.ArgumentsJson, err, Array.Empty<Transaction>(), true);
        }
    }

    private static FilterSpec Filter(JsonElement a, PeriodSpec period) => new()
    {
        Direction = Str(a, "direction") ?? "spending",
        Categories = StrList(a, "categories"),
        DescriptionContains = StrList(a, "description_contains"),
        Account = Str(a, "account"),
        MinAmount = Dec(a, "min_amount"),
        MaxAmount = Dec(a, "max_amount"),
        Period = period,
    };

    private static PeriodSpec Period(JsonElement p) => p.ValueKind != JsonValueKind.Object
        ? throw new ToolArgumentException("period must be an object with a 'type' field.")
        : new PeriodSpec
        {
            Type = Str(p, "type") ?? "all",
            N = Int(p, "n"),
            Year = Int(p, "year"),
            Month = Str(p, "month"),
            StartDate = Str(p, "start_date"),
            EndDate = Str(p, "end_date"),
        };

    private static JsonElement Required(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) ? v : throw new ToolArgumentException($"{name} is required.");

    private static string? Str(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
            _ => null,
        } : null;

    private static decimal? Dec(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        } : null;

    private static List<string>? StrList(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return new List<string> { v.GetString()! };
        if (v.ValueKind != JsonValueKind.Array) return null;
        var list = v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(s => s.Length > 0).ToList();
        return list.Count > 0 ? list : null;
    }
}
