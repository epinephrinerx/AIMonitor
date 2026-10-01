using System.Text.Json;

namespace AIMonitor.Infrastructure.Providers.OpenAi;

/// <summary>
/// Raw (not yet mapped to Domain types) result of one Codex App Server session: the quota result is
/// always present when this type is returned at all (a failure to read it is a hard failure - see
/// <see cref="ICodexAppServerLauncher"/>), while <see cref="Usage"/>/<see cref="HistoryError"/> are a
/// soft, independent outcome so a history-only failure never hides live quota (PAR-004).
/// </summary>
public sealed record CodexAppServerResult(JsonElement RateLimits, JsonElement? Usage, string? HistoryError);
