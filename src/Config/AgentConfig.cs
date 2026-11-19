using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrivateLedgerAgent.Config;

public sealed class AgentConfig
{
    public FoundryConfig Foundry { get; set; } = new();
    public DataConfig Data { get; set; } = new();
    public PrivacyConfig Privacy { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads appsettings.json (or --config), then environment variables, then CLI flags.</summary>
    public static AgentConfig Load(CliArgs args)
    {
        var path = args.Get("config")
                   ?? FirstExisting(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"),
                                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

        var cfg = path is not null && File.Exists(path)
            ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOpts) ?? new AgentConfig()
            : new AgentConfig();
        cfg.ConfigDirectory = path is not null ? Path.GetDirectoryName(Path.GetFullPath(path))! : Directory.GetCurrentDirectory();

        // Environment overrides (keeps endpoints/keys out of files if you prefer).
        cfg.Foundry.Endpoint = Env("FOUNDRY_ENDPOINT") ?? cfg.Foundry.Endpoint;
        cfg.Foundry.Deployment = Env("FOUNDRY_DEPLOYMENT") ?? cfg.Foundry.Deployment;
        cfg.Foundry.ApiStyle = Env("FOUNDRY_API_STYLE") ?? cfg.Foundry.ApiStyle;
        cfg.Foundry.Auth = Env("FOUNDRY_AUTH") ?? cfg.Foundry.Auth;

        // CLI overrides.
        cfg.Foundry.Endpoint = args.Get("endpoint") ?? cfg.Foundry.Endpoint;
        cfg.Foundry.Deployment = args.Get("deployment") ?? args.Get("model") ?? cfg.Foundry.Deployment;
        cfg.Foundry.ApiStyle = args.Get("api-style") ?? cfg.Foundry.ApiStyle;
        cfg.Foundry.ApiVersion = args.Get("api-version") ?? cfg.Foundry.ApiVersion;
        cfg.Foundry.Auth = args.Get("auth") ?? cfg.Foundry.Auth;
        cfg.Data.AsOfDate = args.Get("as-of") ?? cfg.Data.AsOfDate;
        cfg.Data.CategoryRulesPath = args.Get("rules") ?? cfg.Data.CategoryRulesPath;

        var dataPaths = args.GetAll("data");
        if (dataPaths.Count > 0) cfg.Data.Paths = dataPaths;

        return cfg;
    }

    [JsonIgnore] public string ConfigDirectory { get; private set; } = Directory.GetCurrentDirectory();

    public string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ConfigDirectory, path));

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
}

public sealed class FoundryConfig
{
    /// <summary>
    /// Resource endpoint, e.g. https://my-resource.openai.azure.com or https://my-resource.services.ai.azure.com
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Deployment name (AzureOpenAI) or model/deployment name (OpenAIv1, FoundryModels).</summary>
    public string Deployment { get; set; } = "";

    /// <summary>AzureOpenAI | OpenAIv1 | FoundryModels</summary>
    public string ApiStyle { get; set; } = "AzureOpenAI";

    /// <summary>Optional; a sensible default is chosen per ApiStyle.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>ApiKey (reads the env var named by ApiKeyEnvVar) | AzureCli (Entra ID token via `az`).</summary>
    public string Auth { get; set; } = "ApiKey";

    public string ApiKeyEnvVar { get; set; } = "FOUNDRY_API_KEY";

    /// <summary>Set to null for reasoning models that reject temperature.</summary>
    public double? Temperature { get; set; } = 0;

    public int? Seed { get; set; } = 42;
    public int MaxToolRounds { get; set; } = 8;
    public int MaxVerificationRetries { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 90;
}

public sealed class DataConfig
{
    /// <summary>CSV files or folders containing CSV exports.</summary>
    public List<string> Paths { get; set; } = new() { "data" };

    public string CategoryRulesPath { get; set; } = "category-rules.json";

    /// <summary>"today", "latest-transaction", or an explicit yyyy-MM-dd. Anchors relative periods like "last 12 months".</summary>
    public string AsOfDate { get; set; } = "today";

    public List<string> DateFormats { get; set; } = new()
    {
        "yyyy-MM-dd", "MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy", "yyyy/MM/dd", "MM-dd-yyyy",
    };

    /// <summary>NegativeIsOutflow (most banks) | PositiveIsOutflow. Only applies to a single Amount column.</summary>
    public string AmountSign { get; set; } = "NegativeIsOutflow";

    public string CurrencySymbol { get; set; } = "$";

    /// <summary>Categories never counted as spending/income unless explicitly requested (avoids double counting).</summary>
    public List<string> ExcludeFromSpendingAndIncome { get; set; } = new() { "Transfer", "Credit Card Payment" };

    public ColumnMap Columns { get; set; } = new();
}

public sealed class ColumnMap
{
    public List<string> Date { get; set; } = new() { "Date", "Transaction Date", "Posting Date", "Posted Date", "Trans. Date" };
    public List<string> Description { get; set; } = new() { "Description", "Payee", "Merchant", "Name", "Memo", "Details" };
    public List<string> Amount { get; set; } = new() { "Amount", "Transaction Amount" };
    public List<string> Debit { get; set; } = new() { "Debit", "Withdrawal", "Withdrawals", "Money Out" };
    public List<string> Credit { get; set; } = new() { "Credit", "Deposit", "Deposits", "Money In" };
    public List<string> Category { get; set; } = new() { "Category", "Type Category" };
    public List<string> Account { get; set; } = new() { "Account", "Account Number", "Card No.", "Card", "Account Name" };
}

public sealed class PrivacyConfig
{
    /// <summary>
    /// When false the model only ever sees aggregates (totals, counts, breakdowns, ref ids) — never descriptions
    /// of individual transactions. /refs still shows full detail locally.
    /// </summary>
    public bool ShareTransactionDetailsWithModel { get; set; } = true;

    public int MaxRowsToModel { get; set; } = 50;
    public int MaxRefIdsToModel { get; set; } = 200;
    public bool MaskAccountNumbers { get; set; } = true;
    public bool RequireHttps { get; set; } = true;
}

/// <summary>Minimal --key value parser. Repeated keys are allowed (e.g. multiple --data).</summary>
public sealed class CliArgs
{
    private readonly List<(string Key, string? Value)> _items = new();

    public CliArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            var key = args[i][2..];
            string? value = null;
            var eq = key.IndexOf('=');
            if (eq >= 0) { value = key[(eq + 1)..]; key = key[..eq]; }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) value = args[++i];
            _items.Add((key.ToLowerInvariant(), value));
        }
    }

    public bool Has(string key) => _items.Any(i => i.Key == key);
    public string? Get(string key) => _items.LastOrDefault(i => i.Key == key).Value;
    public List<string> GetAll(string key) => _items.Where(i => i.Key == key && i.Value is not null).Select(i => i.Value!).ToList();
}
