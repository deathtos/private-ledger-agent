using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateLedgerAgent.Agent;
using PrivateLedgerAgent.Config;
using PrivateLedgerAgent.Ledger;

namespace PrivateLedgerAgent;

/// <summary>
/// Offline checks (no network): engine arithmetic vs an independent recomputation, and the agent's
/// verification loop driven by a scripted fake model that sometimes hallucinates.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(LedgerEngine engine, LedgerTools tools, AgentConfig cfg)
    {
        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "PASS " : "FAIL ");
            Console.ResetColor();
            Console.WriteLine(name + (detail is null ? "" : $"  ({detail})"));
            if (!ok) failures++;
        }

        var food = engine.Categories.Where(c => c is "Groceries" or "Dining").ToList();
        var last12 = new PeriodSpec { Type = "last_n_months", N = 12 };
        var start = engine.AsOf.AddMonths(-12).AddDays(1);

        // 1. Engine total == independent recomputation, to the cent.
        var (sumObj, evidence) = engine.Sum(new FilterSpec { Direction = "spending", Categories = food, Period = last12 }, 200);
        var sum = JsonSerializer.SerializeToElement(sumObj);
        var expected = engine.All.Where(t => t.Date >= start && t.Date <= engine.AsOf && t.Amount < 0 && food.Contains(t.Category)).Sum(t => -t.Amount);
        Check("sum_transactions matches independent recomputation", sum.GetProperty("total").GetDecimal() == expected, engine.Money(expected));
        Check("evidence rows reconcile to the reported total", evidence.Sum(t => -t.Amount) == expected && evidence.Count == sum.GetProperty("transaction_count").GetInt32());
        Check("every evidence ref points at a real file line", evidence.All(t => engine.ById.ContainsKey(t.RefId) && t.RefId.EndsWith("#L" + t.LineNumber)));

        // 2. Determinism: same question, same bytes.
        var again = JsonSerializer.Serialize(engine.Sum(new FilterSpec { Direction = "spending", Categories = food, Period = last12 }, 200).Result);
        Check("results are byte-for-byte deterministic", again == JsonSerializer.Serialize(sumObj));

        // 3. Breakdown groups reconcile to grand total.
        var bd = JsonSerializer.SerializeToElement(engine.Breakdown(new FilterSpec { Direction = "spending", Period = last12 }, "category", 100).Result);
        var groupSum = bd.GetProperty("groups").EnumerateArray().Sum(g => g.GetProperty("total").GetDecimal());
        Check("breakdown groups sum to grand total", groupSum == bd.GetProperty("grand_total").GetDecimal());

        // 4. Unknown category is an error, not a silent zero.
        var bad = tools.Execute(new ToolCall("x", "sum_transactions", """{"direction":"spending","categories":["Food"],"period":{"type":"all"}}"""));
        Check("unknown category returns an error listing valid categories", bad.IsError && bad.ResultJson.Contains("Groceries"));

        // 5. Card payments / transfers are not double-counted as spending.
        var allSpend = JsonSerializer.SerializeToElement(engine.Sum(new FilterSpec { Direction = "spending", Period = new PeriodSpec() }, 0).Result);
        var transferOut = engine.All.Where(t => t.Amount < 0 && cfg.Data.ExcludeFromSpendingAndIncome.Contains(t.Category, StringComparer.OrdinalIgnoreCase)).Sum(t => -t.Amount);
        var rawOut = engine.All.Where(t => t.Amount < 0).Sum(t => -t.Amount);
        Check("transfers excluded from spending by default", allSpend.GetProperty("total").GetDecimal() == rawOut - transferOut && transferOut > 0);

        // 6. Agent happy path with a well-behaved scripted model.
        var good = new ScriptedModel(Honest);
        var agent = new LedgerAgent(good, tools, engine, cfg.Foundry);
        var a1 = await agent.AskAsync("How much money was used over the course of the last 12 months on food?");
        Check("agent answer verified on happy path", a1.Verified && a1.Text.Contains(engine.Money(expected)), a1.Text.Split('\n')[0]);

        // 7. Hallucinated number is caught; model is made to correct itself.
        var liar = new ScriptedModel(LiesOnce);
        agent = new LedgerAgent(liar, tools, engine, cfg.Foundry);
        var a2 = await agent.AskAsync("How much did I spend on food in the last 12 months?");
        Check("fabricated number rejected, corrected answer verified", a2.Verified && !a2.Text.Contains("1,999.99") && a2.VerificationNote is not null);

        // 8. Fabricated reference id is caught.
        var g = GroundingGuard.Verify("See checking.csv#L9999.", new[] { "{\"ref_ids\":[\"checking.csv#L12\"]}" });
        Check("fabricated reference id rejected", !g.Ok && g.UnknownRefs.Count == 1);

        // 9. Persistent hallucination falls back to deterministic figures.
        var chronic = new ScriptedModel(AlwaysLies);
        agent = new LedgerAgent(chronic, tools, engine, cfg.Foundry);
        var a3 = await agent.AskAsync("How much on food last year?");
        Check("persistent hallucination falls back to verified figures", !a3.Verified && a3.Text.Contains(engine.Money(expected)) && !a3.Text.Contains("1,999.99"));

        Console.WriteLine(failures == 0 ? "\nAll self-tests passed." : $"\n{failures} self-test(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------- scripted models

    private static readonly string SumArgs =
        """{"direction":"spending","categories":["Groceries","Dining"],"period":{"type":"last_n_months","n":12}}""";

    private static ChatResponse Honest(IReadOnlyList<JsonObject> h)
    {
        var lastTool = h.LastOrDefault(m => m["role"]!.GetValue<string>() == "tool");
        if (lastTool is null || !lastTool["content"]!.GetValue<string>().Contains("\"total\"")) return Call("sum_transactions", SumArgs);
        var r = JsonDocument.Parse(lastTool["content"]!.GetValue<string>()).RootElement;
        return Say($"You spent {r.GetProperty("total_formatted").GetString()} on food (Groceries, Dining) during the " +
                   $"{r.GetProperty("period").GetProperty("label").GetString()}, across {r.GetProperty("transaction_count").GetInt32()} transactions.");
    }

    private static ChatResponse LiesOnce(IReadOnlyList<JsonObject> h) =>
        h.Any(m => (m["content"]?.ToString() ?? "").StartsWith("[verifier]")) || !h.Any(m => m["role"]!.GetValue<string>() == "tool")
            ? Honest(h)
            : Say("You spent about $1,999.99 on food over the last 12 months.");

    private static ChatResponse AlwaysLies(IReadOnlyList<JsonObject> h) =>
        h.Any(m => m["role"]!.GetValue<string>() == "tool") ? Say("Roughly $1,999.99.") : Call("sum_transactions", SumArgs);

    private static int _id;
    private static ChatResponse Call(string name, string args)
    {
        var id = $"call_{++_id}";
        var msg = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = id, ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args },
            }),
        };
        return new ChatResponse(null, new[] { new ToolCall(id, name, args) }, msg);
    }

    private static ChatResponse Say(string text) =>
        new(text, Array.Empty<ToolCall>(), new JsonObject { ["role"] = "assistant", ["content"] = text });

    private sealed class ScriptedModel(Func<IReadOnlyList<JsonObject>, ChatResponse> script) : IChatModel
    {
        public Task<ChatResponse> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, CancellationToken ct) =>
            Task.FromResult(script(messages));
    }
}
