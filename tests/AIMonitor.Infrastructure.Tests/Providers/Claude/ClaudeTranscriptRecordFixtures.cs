using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>Builds synthetic Claude Code transcript records shared by
/// <see cref="ClaudeTranscriptStore"/>'s test files. Never touches a real transcript, credential, or
/// network resource.</summary>
internal static class ClaudeTranscriptRecordFixtures
{
    public const string When = "2026-09-14T10:00:00Z";
    public const string DefaultCwd = "C:\\work\\project";
    public const string DefaultModel = "claude-opus-4";

    /// <summary>A well-formed assistant record, with <paramref name="usageOverrides"/> replacing or
    /// adding fields under <c>message.usage</c> so a test can make exactly one field bad.</summary>
    public static JsonElement Record(
        string messageId = "msg-1",
        JsonObject? usageOverrides = null,
        string? model = DefaultModel,
        string? timestamp = When,
        string? cwd = DefaultCwd)
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = 100,
            ["output_tokens"] = 200,
            ["cache_read_input_tokens"] = 300,
            ["cache_creation_input_tokens"] = 400,
        };

        if (usageOverrides is not null)
        {
            foreach (var pair in usageOverrides.ToArray())
            {
                usage[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var root = new JsonObject
        {
            ["type"] = "assistant",
            ["timestamp"] = timestamp,
            ["cwd"] = cwd,
            ["message"] = new JsonObject
            {
                ["id"] = messageId,
                ["model"] = model,
                ["usage"] = usage,
            },
        };

        return Parse(root.ToJsonString());
    }

    /// <summary>A record with a fixed token count, for tests that only care about model/day grouping.</summary>
    public static JsonElement Record(string model, long inputTokens, string messageIdSuffix, int daysAgo = 0)
    {
        var when = DateTimeOffset.Parse(When, System.Globalization.CultureInfo.InvariantCulture).AddDays(-daysAgo);
        var root = new JsonObject
        {
            ["type"] = "assistant",
            ["timestamp"] = when.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["cwd"] = DefaultCwd,
            ["message"] = new JsonObject
            {
                ["id"] = $"msg-{model}-{messageIdSuffix}",
                ["model"] = model,
                ["usage"] = new JsonObject { ["input_tokens"] = inputTokens, ["output_tokens"] = 0 },
            },
        };

        return Parse(root.ToJsonString());
    }

    public static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Writes <paramref name="records"/> as one JSON line each, UTF-8, LF-terminated -
    /// matching how Claude Code appends to a transcript.</summary>
    public static void WriteJsonl(string path, params JsonElement[] records)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
        foreach (var record in records)
        {
            writer.Write(record.GetRawText());
            writer.Write('\n');
        }
    }

    public static void AppendJsonl(string path, params JsonElement[] records)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
        foreach (var record in records)
        {
            writer.Write(record.GetRawText());
            writer.Write('\n');
        }
    }

    public static void AppendRawLine(string path, string rawLine)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" };
        writer.Write(rawLine);
        writer.Write('\n');
    }
}
