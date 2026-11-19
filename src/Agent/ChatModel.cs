using System.Text.Json.Nodes;

namespace PrivateLedgerAgent.Agent;

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);

public sealed record ChatResponse(string? Content, IReadOnlyList<ToolCall> ToolCalls, JsonObject AssistantMessage);

/// <summary>Stateless chat-completions abstraction. Every call carries the full (in-memory) history; nothing is stored server-side.</summary>
public interface IChatModel
{
    Task<ChatResponse> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, CancellationToken ct);
}
