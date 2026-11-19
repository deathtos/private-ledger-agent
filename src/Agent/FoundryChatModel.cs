using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateLedgerAgent.Config;

namespace PrivateLedgerAgent.Agent;

/// <summary>
/// Calls an Azure AI Foundry / Azure OpenAI chat-completions endpoint over HTTPS with plain HttpClient.
/// Deliberately does NOT use the Foundry Agent Service (threads, files, vector stores), because those persist
/// conversation data server-side. Each request is self-contained and the history lives only in this process.
/// </summary>
public sealed class FoundryChatModel : IChatModel, IDisposable
{
    private readonly FoundryConfig _cfg;
    private readonly HttpClient _http;
    private readonly Uri _uri;
    private readonly bool _sendModelInBody;
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public FoundryChatModel(FoundryConfig cfg, bool requireHttps)
    {
        _cfg = cfg;
        if (string.IsNullOrWhiteSpace(cfg.Endpoint)) throw new InvalidOperationException("Foundry endpoint is not set (--endpoint or FOUNDRY_ENDPOINT).");
        if (string.IsNullOrWhiteSpace(cfg.Deployment)) throw new InvalidOperationException("Deployment/model is not set (--deployment or FOUNDRY_DEPLOYMENT).");

        var baseUri = new Uri(cfg.Endpoint.TrimEnd('/') + "/");
        if (baseUri.AbsolutePath.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "That looks like a Foundry *project* (Agent Service) endpoint. Use the resource endpoint instead, " +
                "e.g. https://<resource>.services.ai.azure.com or https://<resource>.openai.azure.com — the Agent Service stores threads server-side.");
        var isLocal = baseUri.IsLoopback;
        if (requireHttps && baseUri.Scheme != Uri.UriSchemeHttps && !isLocal)
            throw new InvalidOperationException("Endpoint must use HTTPS.");

        var root = $"{baseUri.Scheme}://{baseUri.Authority}";
        (_uri, _sendModelInBody) = cfg.ApiStyle.ToLowerInvariant() switch
        {
            "azureopenai" => (new Uri($"{root}/openai/deployments/{Uri.EscapeDataString(cfg.Deployment)}/chat/completions?api-version={cfg.ApiVersion ?? "2024-10-21"}"), false),
            "openaiv1" => (new Uri($"{root}/openai/v1/chat/completions" + (cfg.ApiVersion is { } v ? $"?api-version={v}" : "")), true),
            "foundrymodels" => (new Uri($"{root}/models/chat/completions?api-version={cfg.ApiVersion ?? "2024-05-01-preview"}"), true),
            _ => throw new InvalidOperationException("Foundry.ApiStyle must be AzureOpenAI, OpenAIv1 or FoundryModels."),
        };

        _http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds),
        };
    }

    public string Target => $"{_uri.Host} ({_cfg.ApiStyle}, {_cfg.Deployment})";

    public async Task<ChatResponse> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()),
            ["tools"] = tools.DeepClone(),
            ["tool_choice"] = "auto",
        };
        if (_sendModelInBody) body["model"] = _cfg.Deployment;
        if (_cfg.Temperature is { } t) body["temperature"] = t;
        if (_cfg.Seed is { } s) body["seed"] = s;

        var payload = body.ToJsonString();
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _uri) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            await AuthorizeAsync(req, ct);
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode) return Parse(text);

            var transient = resp.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
            if (transient && attempt < 4)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait, ct);
                continue;
            }
            throw new HttpRequestException($"Foundry returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(ErrorMessage(text), 500)}");
        }
    }

    private static ChatResponse Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var choice = root["choices"]?[0] ?? throw new InvalidOperationException("Response contained no choices.");
        if (choice["finish_reason"]?.GetValue<string>() == "content_filter")
            throw new InvalidOperationException("The response was blocked by the content filter.");
        var msg = choice["message"]!.AsObject();

        var calls = new List<ToolCall>();
        if (msg["tool_calls"] is JsonArray arr)
        {
            foreach (var c in arr)
            {
                calls.Add(new ToolCall(
                    c!["id"]!.GetValue<string>(),
                    c["function"]!["name"]!.GetValue<string>(),
                    c["function"]!["arguments"]?.GetValue<string>() ?? "{}"));
            }
        }

        var content = msg["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var assistant = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (msg["tool_calls"] is JsonArray tc) assistant["tool_calls"] = tc.DeepClone();
        return new ChatResponse(content, calls, assistant);
    }

    private async Task AuthorizeAsync(HttpRequestMessage req, CancellationToken ct)
    {
        switch (_cfg.Auth.ToLowerInvariant())
        {
            case "apikey":
                var key = Environment.GetEnvironmentVariable(_cfg.ApiKeyEnvVar);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException($"Environment variable {_cfg.ApiKeyEnvVar} is not set (or use --auth AzureCli for Entra ID).");
                req.Headers.Add("api-key", key);
                break;
            case "azurecli":
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetCliTokenAsync(ct));
                break;
            default:
                throw new InvalidOperationException("Foundry.Auth must be ApiKey or AzureCli.");
        }
    }

    /// <summary>Entra ID token from the signed-in Azure CLI — no secrets on disk, works with RBAC-only resources.</summary>
    private async Task<string> GetCliTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;

        const string azArgs = "account get-access-token --resource https://cognitiveservices.azure.com -o json";
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c az " + azArgs)
            : new ProcessStartInfo("az", azArgs);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Azure CLI (az).");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct);
        var stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException("az account get-access-token failed. Run `az login`. " + Truncate(stderr, 300));

        using var doc = JsonDocument.Parse(stdout);
        _token = doc.RootElement.GetProperty("accessToken").GetString();
        _tokenExpires = doc.RootElement.TryGetProperty("expires_on", out var eo) && eo.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(eo.GetInt64()).AddMinutes(-5)
            : DateTimeOffset.UtcNow.AddMinutes(30);
        return _token!;
    }

    private static string ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body; }
        catch (JsonException) { return body; }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Dispose() => _http.Dispose();
}
