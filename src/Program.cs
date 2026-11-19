using System.Globalization;
using PrivateLedgerAgent;
using PrivateLedgerAgent.Agent;
using PrivateLedgerAgent.Config;
using PrivateLedgerAgent.Ledger;

var cli = new CliArgs(args);
if (cli.Has("help") || cli.Has("h"))
{
    Console.WriteLine(Help.Text);
    return 0;
}

var cfg = AgentConfig.Load(cli);

// ---- Load grounding data (local only) ----
var rules = CategoryRules.Load(cfg.Resolve(cfg.Data.CategoryRulesPath));
var load = LedgerLoader.Load(cfg.Data.Paths.Select(cfg.Resolve), cfg.Data, rules, cfg.Privacy.MaskAccountNumbers);
if (load.Transactions.Count == 0)
{
    Console.Error.WriteLine("No transactions loaded. Point --data at a CSV file or folder.");
    foreach (var w in load.Warnings) Console.Error.WriteLine("  ! " + w);
    return 2;
}

var asOf = cfg.Data.AsOfDate.ToLowerInvariant() switch
{
    "today" => DateOnly.FromDateTime(DateTime.Today),
    "latest-transaction" => load.Transactions[^1].Date,
    var s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture),
};

var engine = new LedgerEngine(load.Transactions, asOf, cfg.Data.ExcludeFromSpendingAndIncome, load.Files, cfg.Data.CurrencySymbol);
var tools = new LedgerTools(engine, cfg.Privacy);

if (cli.Has("self-test")) return await SelfTest.RunAsync(engine, tools, cfg);

Console.WriteLine($"Loaded {load.Transactions.Count} transactions from {load.Files.Count} file(s): {string.Join(", ", load.Files)}");
Console.WriteLine($"Coverage {load.Transactions[0].Date:yyyy-MM-dd} → {load.Transactions[^1].Date:yyyy-MM-dd}; as-of {asOf:yyyy-MM-dd}; categories: {string.Join(", ", engine.Categories)}");
if (load.Warnings.Count > 0)
{
    Console.WriteLine($"{load.Warnings.Count} load warning(s):");
    foreach (var w in load.Warnings.Take(10)) Console.WriteLine("  ! " + w);
}

if (cli.Has("overview"))
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(engine.Overview(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

// ---- Model ----
FoundryChatModel model;
try { model = new FoundryChatModel(cfg.Foundry, cfg.Privacy.RequireHttps); }
catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return 2; }
using var _ = model;

Console.WriteLine($"Model: {model.Target}. Only tool results are sent; raw files never leave this machine.");
Console.WriteLine($"Transaction-level detail to model: {(cfg.Privacy.ShareTransactionDetailsWithModel ? "on" : "off (aggregates only)")}");
var agent = new LedgerAgent(model, tools, engine, cfg.Foundry);

// ---- One-shot mode ----
if (cli.Get("question") is { } oneShot)
{
    var ok = await AskAndPrint(oneShot);
    if (cli.Has("show-refs")) Console.WriteLine(Console2.RenderRefs(agent.LastToolCalls, engine));
    return ok ? 0 : 1;
}

// ---- Interactive mode ----
Console.WriteLine("Ask a question about the ledger. Commands: /refs  /audit  /overview  /reset  /help  /exit");
while (true)
{
    Console.Write("\n> ");
    var line = Console.ReadLine();
    if (line is null) break;
    line = line.Trim();
    if (line.Length == 0) continue;

    switch (line.ToLowerInvariant())
    {
        case "/exit" or "/quit": return 0;
        case "/help": Console.WriteLine(Help.Commands); continue;
        case "/reset": agent.Reset(); Console.WriteLine("Conversation cleared (nothing was stored anywhere)."); continue;
        case "/refs": Console.WriteLine(Console2.RenderRefs(agent.LastToolCalls, engine)); continue;
        case "/audit": Console.WriteLine(Console2.RenderAudit(agent.LastToolCalls)); continue;
        case "/overview":
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(engine.Overview(), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            continue;
    }

    await AskAndPrint(line);
}
return 0;

async Task<bool> AskAndPrint(string question)
{
    try
    {
        var answer = await agent.AskAsync(question);
        Console.WriteLine();
        Console.WriteLine(answer.Text);
        Console.ForegroundColor = answer.Verified ? ConsoleColor.DarkGreen : ConsoleColor.Yellow;
        var calls = answer.ToolCalls.Count(c => !c.IsError);
        Console.WriteLine(answer.Verified
            ? $"\n[✓ all numbers verified against {calls} tool result(s){(answer.VerificationNote is { } n ? $", {n}" : "")} — /refs for sources]"
            : $"\n[! narrative failed verification ({answer.VerificationNote}); showing deterministic figures]");
        Console.ResetColor();
        return answer.Verified;
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Error: " + ex.Message);
        Console.ResetColor();
        return false;
    }
}

namespace PrivateLedgerAgent
{
    internal static class Console2
    {
        /// <summary>Printed straight from the ledger — the model is not involved.</summary>
        public static string RenderRefs(IReadOnlyList<ToolExecution> calls, LedgerEngine engine)
        {
            var withEvidence = calls.Where(c => c.Evidence.Count > 0).ToList();
            if (withEvidence.Count == 0) return "No transactions were used for the last answer.";
            var sb = new System.Text.StringBuilder();
            var i = 0;
            foreach (var c in withEvidence)
            {
                i++;
                var outflow = c.Evidence.Where(t => t.IsOutflow).Sum(t => -t.Amount);
                var inflow = c.Evidence.Where(t => t.IsInflow).Sum(t => t.Amount);
                sb.AppendLine($"── Source set {i}: {c.Name} {c.ArgumentsJson}");
                sb.AppendLine($"   {c.Evidence.Count} transaction(s); money out {engine.Money(outflow)}, money in {engine.Money(inflow)}");
                sb.AppendLine($"   {"Reference",-34} {"Date",-10} {"Amount",12}  {"Category",-16} Description");
                foreach (var t in c.Evidence.OrderBy(t => t.Date).ThenBy(t => t.RefId, StringComparer.Ordinal))
                    sb.AppendLine($"   {t.RefId,-34} {t.Date:yyyy-MM-dd} {engine.Money(t.Amount),12}  {Trim(t.Category, 16),-16} {t.Description}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string RenderAudit(IReadOnlyList<ToolExecution> calls)
        {
            if (calls.Count == 0) return "No tool calls for the last answer.";
            return string.Join(Environment.NewLine, calls.Select((c, n) =>
                $"{n + 1}. {c.Name}({c.ArgumentsJson}){(c.IsError ? "  → ERROR " + c.ResultJson : $"  → {c.Evidence.Count} transaction(s)")}"));
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    }

    internal static class Help
    {
        public const string Commands = """
            /refs      Print every transaction (file#line, date, amount, description) behind the last answer — straight from your files
            /audit     Show the exact tool calls the model made for the last answer
            /overview  Show files, coverage and categories (local only)
            /reset     Clear the in-memory conversation
            /exit      Quit
            """;

        public const string Text = $"""
            ledger-agent — private, grounded Q&A over bank CSV exports using an Azure AI Foundry model

            Usage:
              ledger-agent [--data <file-or-folder>]... [--endpoint <url>] [--deployment <name>]
                           [--api-style AzureOpenAI|OpenAIv1|FoundryModels] [--auth ApiKey|AzureCli]
                           [--as-of today|latest-transaction|yyyy-MM-dd] [--rules category-rules.json]
                           [--config appsettings.json] [--question "..."] [--show-refs] [--overview] [--self-test]

            Environment: FOUNDRY_ENDPOINT, FOUNDRY_DEPLOYMENT, FOUNDRY_API_KEY, FOUNDRY_API_STYLE, FOUNDRY_AUTH

            Interactive commands:
            {Commands}
            """;
    }
}
