using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Builds synthetic Claude Code transcript records for SPEC R-014 "P2b detailed". Every field the spec
/// names (<c>message.id</c>, <c>message.model</c>, <c>requestId</c>, <c>sessionId</c>,
/// <c>isSidechain</c>, <c>version</c>, <c>usage.speed</c>, <c>usage.iterations[]</c>) can be present,
/// absent, or an empty string independently, so a test can change exactly one field while holding the
/// rest fixed. Content is invented; no real transcript, credential, or network resource is used
/// (Docs/TESTING.md §5).
/// </summary>
/// <remarks>
/// The cache write is emitted as <c>usage.cache_creation_input_tokens</c> (the aggregate spelling that A8
/// prices entirely as a 5-minute write); the split <c>cache_creation.{ephemeral_5m,ephemeral_1h}</c>
/// spelling is already covered by the ephemeral-split cases in
/// <see cref="ClaudeTranscriptStoreIngestTests"/> and is used inside advisor entries by
/// <see cref="AdvisorSplitCacheWrite"/>.
/// </remarks>
internal sealed class TranscriptRecordBuilder
{
    /// <summary>The instant most ingest tests date their records from: 6 days before the fixed "now"
    /// (2026-09-20, UTC) they use, so a 7-day window sees them and a 6-day window does not.</summary>
    public const string DefaultWhen = "2026-09-14T10:00:00Z";

    /// <summary>A model with an exact published rate (spec P2a table: 5 / 25 / 0.50 per MTok).</summary>
    public const string DefaultModel = "claude-opus-5";

    public const string DefaultCwd = "C:\\work\\project";

    private string? _id = "msg-1";
    private string? _model = DefaultModel;
    private string? _requestId;
    private string? _sessionId;
    private bool? _isSidechain;
    private string? _version;
    private string? _timestamp = DefaultWhen;
    private string? _cwd = DefaultCwd;
    private string? _speed;
    private long _input;
    private long _output;
    private long _cacheRead;
    private long _cacheWrite;
    private readonly List<JsonNode?> _iterations = [];

    private TranscriptRecordBuilder()
    {
    }

    /// <summary>An assistant record with a <c>message.id</c>, an exact model and no tokens. Add tokens
    /// with <see cref="Tokens"/>; anything not set explicitly is left out of the JSON entirely.</summary>
    public static TranscriptRecordBuilder Create(string? id = "msg-1", string? model = DefaultModel)
    {
        var builder = new TranscriptRecordBuilder();
        builder._id = id;
        builder._model = model;
        return builder;
    }

    /// <summary>A record carrying only <paramref name="input"/>/<paramref name="output"/> tokens, for
    /// tests whose only variable is the survivor comparison.</summary>
    public static TranscriptRecordBuilder WithTokens(
        long input,
        long output,
        string? id = "msg-1",
        string? model = DefaultModel) =>
        Create(id, model).Tokens(input, output);

    public TranscriptRecordBuilder Id(string? id)
    {
        _id = id;
        return this;
    }

    public TranscriptRecordBuilder Model(string? model)
    {
        _model = model;
        return this;
    }

    public TranscriptRecordBuilder RequestId(string? requestId)
    {
        _requestId = requestId;
        return this;
    }

    public TranscriptRecordBuilder SessionId(string? sessionId)
    {
        _sessionId = sessionId;
        return this;
    }

    /// <summary>Emits <c>isSidechain</c>; <see langword="null"/> leaves the field out, which P2b.4 treats
    /// the same as <see langword="false"/> ("<c>side</c> = isSidechain == true").</summary>
    public TranscriptRecordBuilder Sidechain(bool? sidechain)
    {
        _isSidechain = sidechain;
        return this;
    }

    public TranscriptRecordBuilder Version(string? version)
    {
        _version = version;
        return this;
    }

    /// <summary>Emits <c>timestamp</c>; <see langword="null"/> leaves the field out entirely.</summary>
    public TranscriptRecordBuilder At(string? timestamp)
    {
        _timestamp = timestamp;
        return this;
    }

    public TranscriptRecordBuilder Cwd(string? cwd)
    {
        _cwd = cwd;
        return this;
    }

    public TranscriptRecordBuilder Speed(string? speed)
    {
        _speed = speed;
        return this;
    }

    /// <summary>Sets this record's four token fields; unspecified ones are zero, which transcripts write
    /// as an explicit 0 or omit - the store reads both as zero.</summary>
    public TranscriptRecordBuilder Tokens(
        long input = 0,
        long output = 0,
        long cacheRead = 0,
        long cacheWrite = 0)
    {
        _input = input;
        _output = output;
        _cacheRead = cacheRead;
        _cacheWrite = cacheWrite;
        return this;
    }

    /// <summary>Appends one <c>message.usage.iterations[]</c> entry of type <c>advisor_message</c> with
    /// its own model and token fields (B16).</summary>
    public TranscriptRecordBuilder Advisor(
        string? model,
        long input = 0,
        long output = 0,
        long cacheRead = 0,
        long cacheWrite = 0)
    {
        var iteration = NewAdvisorIteration(model);
        iteration["input_tokens"] = input;
        iteration["output_tokens"] = output;
        iteration["cache_read_input_tokens"] = cacheRead;
        iteration["cache_creation_input_tokens"] = cacheWrite;
        _iterations.Add(iteration);
        return this;
    }

    /// <summary>The same advisor entry using the split cache-write spelling, which B16 also names.</summary>
    public TranscriptRecordBuilder AdvisorSplitCacheWrite(
        string? model,
        long input = 0,
        long output = 0,
        long cacheRead = 0,
        long fiveMinuteWrite = 0,
        long oneHourWrite = 0)
    {
        var iteration = NewAdvisorIteration(model);
        iteration["input_tokens"] = input;
        iteration["output_tokens"] = output;
        iteration["cache_read_input_tokens"] = cacheRead;
        iteration["cache_creation"] = new JsonObject
        {
            ["ephemeral_5m_input_tokens"] = fiveMinuteWrite,
            ["ephemeral_1h_input_tokens"] = oneHourWrite,
        };
        _iterations.Add(iteration);
        return this;
    }

    /// <summary>Appends an iteration that is deliberately not an advisor entry, so a test can show that
    /// B16's <c>&lt;id&gt;:advisor:&lt;n&gt;</c> counter advances on advisor entries only.</summary>
    public TranscriptRecordBuilder OtherIteration(
        string model = DefaultModel,
        long input = 1,
        long output = 1)
    {
        _iterations.Add(new JsonObject
        {
            ["type"] = "normal_message",
            ["model"] = model,
            ["input_tokens"] = input,
            ["output_tokens"] = output,
        });
        return this;
    }

    /// <summary>Appends a raw iteration entry - an object without a type, a number, a string,
    /// <c>null</c> - for the shapes B16 says are skipped alone.</summary>
    public TranscriptRecordBuilder RawIteration(string json)
    {
        _iterations.Add(JsonNode.Parse(json));
        return this;
    }

    public JsonObject ToJsonNode()
    {
        var root = new JsonObject { ["type"] = "assistant" };

        if (_timestamp is not null)
        {
            root["timestamp"] = _timestamp;
        }

        if (_cwd is not null)
        {
            root["cwd"] = _cwd;
        }

        if (_requestId is not null)
        {
            root["requestId"] = _requestId;
        }

        if (_sessionId is not null)
        {
            root["sessionId"] = _sessionId;
        }

        if (_isSidechain is not null)
        {
            root["isSidechain"] = _isSidechain.Value;
        }

        if (_version is not null)
        {
            root["version"] = _version;
        }

        var usage = new JsonObject
        {
            ["input_tokens"] = _input,
            ["output_tokens"] = _output,
            ["cache_read_input_tokens"] = _cacheRead,
            ["cache_creation_input_tokens"] = _cacheWrite,
        };

        if (_speed is not null)
        {
            usage["speed"] = _speed;
        }

        if (_iterations.Count > 0)
        {
            var iterations = new JsonArray();
            foreach (var iteration in _iterations)
            {
                iterations.Add(iteration?.DeepClone());
            }

            usage["iterations"] = iterations;
        }

        var message = new JsonObject { ["usage"] = usage };
        if (_id is not null)
        {
            message["id"] = _id;
        }

        if (_model is not null)
        {
            message["model"] = _model;
        }

        root["message"] = message;
        return root;
    }

    /// <summary>The record as a parsed <see cref="JsonElement"/>, for
    /// <see cref="ClaudeTranscriptStore.TryIngest"/>.</summary>
    public JsonElement Build() => JsonDocument.Parse(ToJsonNode().ToJsonString()).RootElement.Clone();

    /// <summary>The record as one JSONL line, for writing a real transcript file.</summary>
    public string Line() => ToJsonNode().ToJsonString();

    private static JsonObject NewAdvisorIteration(string? model)
    {
        var iteration = new JsonObject { ["type"] = "advisor_message" };
        if (model is not null)
        {
            iteration["model"] = model;
        }

        return iteration;
    }
}
