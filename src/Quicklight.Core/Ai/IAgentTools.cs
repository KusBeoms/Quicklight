using System.Text.Json.Nodes;

namespace Quicklight.Core.Ai;

/// <param name="Schema">JSON Schema of the parameters (an object schema).</param>
/// <param name="ReadOnly">Runs without asking; everything else needs the user's consent each time.</param>
public sealed record AgentTool(string Name, string Description, JsonObject Schema, bool ReadOnly);

/// <summary>A set of tools the model may call (computer control, the MCP search tools).</summary>
public interface IAgentTools
{
    IReadOnlyList<AgentTool> Tools { get; }

    /// <summary>The result as text for the model. Failures are returned as text too, so the model can react to them.</summary>
    Task<string> CallAsync(string name, JsonObject args, CancellationToken ct);

    /// <summary>One short Korean line for the consent dialog, e.g. "메모장에서 '저장' 버튼 클릭".</summary>
    string Describe(string name, JsonObject args);
}

/// <summary>Asks the user before a tool changes anything.</summary>
public interface IConsent
{
    Task<bool> ConfirmAsync(string summary, CancellationToken ct);
}
