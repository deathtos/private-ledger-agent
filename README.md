# ledger-agent — a private, grounded bank-ledger agent (C# / .NET 8)

Ask plain-English questions about your bank exports from the console. Answers come from your CSV files and nothing else.
The numbers are exact, and every one can be traced to a `file.csv#L<line>` reference.

```
> How much money was used over the course of the last 12 months on food

You spent $16,152.63 on food (Groceries, Dining) from 2025-11-20 to 2026-11-19, an average of $1,346.05 per month.

[✓ all numbers verified against 2 tool result(s) — /refs for sources]
```

## How it guarantees "no variance in the numbers"

| Layer | What it does |
|---|---|
| **Deterministic engine** (`LedgerEngine`) | All arithmetic uses `System.Decimal` in C#: totals, averages, shares, differences, date ranges. The model never does math. Same question → byte-identical results. |
| **Deterministic categories** (`category-rules.json`) | Categories come from your file or from keyword rules, never from the model. If the model asks for a category that doesn't exist (e.g. "Food"), it gets an error listing the real ones. It never gets a silent $0. |
| **Tool-only grounding** | The model can only see the data through 5 tools: `get_dataset_overview`, `sum_transactions`, `breakdown`, `compare_periods`, `list_transactions`. |
| **Grounding guard** (`GroundingGuard`) | Before you see an answer, every number and every reference id in it is checked against the tool results. If anything wasn't returned by a tool, the draft is rejected and the model has to correct it. If it keeps failing, the app prints the deterministic figures instead. |
| **Local references** (`/refs`) | Prints every transaction behind the last answer (file, line, date, amount, description) straight from your files. The model isn't involved. |

Relative periods ("last 12 months", "year to date") are resolved in C# against an as-of date. Each answer states the exact
date range and the categories it covered. Transfers and credit-card payments are left out of spending by default, so
paying your card from checking isn't counted twice.

## Privacy model

- **Raw files never leave the machine.** The model only receives the tool results it asks for (aggregates, plus individual rows if you allow it).
- **No server-side state.** This app calls the stateless chat-completions API directly. It deliberately avoids the Foundry
  *Agent Service* (threads, file uploads, vector stores), which stores conversation data in the service. If you paste a
  `/api/projects/...` endpoint, the app refuses it.
- **History stays in memory.** Nothing is written to disk, and `/reset` clears it.
- **Account numbers are masked** to their last 4 digits when loaded (`Checking ****9931`).
- **Aggregates-only mode:** set `Privacy.ShareTransactionDetailsWithModel: false` and the model never sees a single
  transaction description. `/refs` still shows full detail locally.
- **No third-party packages.** Plain `HttpClient` + `System.Text.Json`. The only outbound call goes to your endpoint, and it must be HTTPS.
- **Secrets stay out of files.** The API key is read from an environment variable, or use Entra ID (`--auth AzureCli`) with RBAC and no key at all.
- **Prompt-injection hardening.** Transaction descriptions are treated as untrusted data, and the agent declines off-topic questions.

For stronger isolation on the Azure side, consider a private endpoint with public network access disabled, Entra-only
auth (disable local keys), and, if eligible, applying for modified abuse monitoring so prompts aren't retained for review.

## Setup

```bash
dotnet build -c Release
dotnet bin/Release/net8.0/ledger-agent.dll --self-test          # offline checks against the sample data
```

### Point it at your Foundry deployment

```bash
# API key auth
export FOUNDRY_API_KEY=...            # PowerShell: $env:FOUNDRY_API_KEY="..."
dotnet run -- --endpoint https://my-resource.openai.azure.com --deployment gpt-4.1 --data ./my-exports

# Entra ID auth (after `az login`; needs "Cognitive Services OpenAI User" on the resource)
dotnet run -- --endpoint https://my-resource.services.ai.azure.com --deployment gpt-4.1 --auth AzureCli --data ./my-exports
```

| `--api-style` | URL called | Use for |
|---|---|---|
| `AzureOpenAI` (default) | `/openai/deployments/{deployment}/chat/completions` | Azure OpenAI deployments |
| `OpenAIv1` | `/openai/v1/chat/completions` | The v1 API on Azure OpenAI / Foundry |
| `FoundryModels` | `/models/chat/completions` | Non-OpenAI Foundry models that support tool calling |

For reasoning models that reject `temperature`, set `Foundry.Temperature` to `null`.

### One-shot vs interactive

```bash
dotnet run -- --question "What did I spend on dining vs last year?" --show-refs
dotnet run --                                  # REPL: /refs /audit /overview /reset /exit
```

## Your data

Put bank CSV exports in `data/` (or pass `--data` one or more times). Columns are detected by header name. You can
extend the lists in `Data.Columns` in `appsettings.json`. It supports:

- a single signed `Amount` column (`AmountSign`: `NegativeIsOutflow` or `PositiveIsOutflow`), or separate `Debit` / `Credit` columns
- `$1,234.56`, `(12.34)` and `12.34-` amount formats; date formats from `Data.DateFormats`
- an optional `Category` column (renamed via `Aliases` in `category-rules.json`); rows without one are categorised by keyword rules

Rows that can't be parsed are reported at startup with their `file#line`. They're never silently dropped.

`AsOfDate` anchors "last N months": `today` (default), `latest-transaction`, or a fixed `yyyy-MM-dd` for reproducible reports.

## Project layout

```
src/Ledger/   CsvParser, LedgerLoader, CategoryRules, LedgerEngine   ← deterministic, offline
src/Agent/    FoundryChatModel (REST), LedgerTools (schemas+dispatch), GroundingGuard, LedgerAgent (tool loop)
src/Program.cs, src/SelfTest.cs
```

Note: the self-test asserts against the bundled sample data (it expects Groceries/Dining and card-payment rows). Run it with the default `data/` folder.
