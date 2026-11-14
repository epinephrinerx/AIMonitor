using System.Globalization;
using System.Text.Json;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Incrementally parsed, pre-aggregated view of the local Claude Code transcripts under one or more
/// <c>projects</c> directories (PAR-006). Read-only: never creates, modifies, or deletes a transcript
/// file. Aggregates on ingest - a record is folded into per-day/model/project counters and then
/// discarded, never retained - so the aggregate state is bounded by (days retained x
/// models/projects), not by history length. The only history-sized state is the de-duplication
/// seen-set, which is bounded to <see cref="MaxSeenRecords"/> entries and stores each key as a
/// 64-bit hash (never the key text) plus one shared instance per distinct model/project label; at
/// 400,000 retained records the measured managed heap is about 94 MB after a full GC. Every public
/// member after construction is safe to call repeatedly across the
/// lifetime of one Claude provider client; the byte offset and de-duplication state recorded by
/// <see cref="RefreshAsync"/> are this store's explicit checkpoint; there is no separate synchronous
/// re-scan path.
/// </summary>
public sealed class ClaudeTranscriptStore
{
    /// <summary>Longest range the UI offers is 90 days; beyond this, day buckets are dropped.</summary>
    private const int RetainedDays = 400;

    /// <summary>Bounds the de-duplication set on a machine with very long history. Oldest records are
    /// evicted first (together with their keys); an evicted record can only be re-counted if its
    /// transcript is re-read from offset zero (e.g. after rotation), matching
    /// <see cref="RefreshAsync"/>'s own rotation handling.</summary>
    private const int MaxSeenRecords = 400_000;

    /// <summary>Models past this many fold into "Other" in a built <see cref="UsageHistory"/>, so a
    /// chart's categorical palette is never asked to render more series than it has colours for.</summary>
    private const int MaxChartedModels = 8;

    private const string OtherModelLabel = "Other";
    private const string UnknownProjectLabel = "(unknown)";
    private const string SyntheticModelId = "<synthetic>";
    private const string SyntheticModelLabel = "(synthetic)";
    private const int ChunkSize = 8192;

    private static readonly byte[] AssistantMarkerTight = "\"type\":\"assistant\""u8.ToArray();
    private static readonly byte[] AssistantMarkerSpaced = "\"type\": \"assistant\""u8.ToArray();

    private readonly string[] _roots;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly Func<string, Stream> _openFile;
    private readonly Dictionary<string, long> _offsets = new(StringComparer.Ordinal);
    private readonly SeenRecordSet _seen;
    private readonly Dictionary<DateOnly, Dictionary<string, UsageCounters>> _byDayModel = [];
    private readonly Dictionary<DateOnly, Dictionary<string, UsageCounters>> _byDayProject = [];

    /// <summary>B12: one shared instance per distinct label. Model and project names repeat on
    /// every record, so keeping the strings from the JSON parse would retain a copy per record
    /// through the seen-set and the day buckets.</summary>
    private readonly Dictionary<string, string> _modelLabels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _projectLabels = new(StringComparer.Ordinal);

    /// <param name="root">The <c>projects</c> directory to scan; see <see cref="ClaudeTranscriptPathResolver"/>.
    /// Does not need to exist yet - a missing directory is an expected, reported condition, not a
    /// construction-time error, since Claude Code may not have written any transcripts yet.</param>
    /// <param name="clock">Source of "now" for window queries and retention pruning.</param>
    /// <param name="localTimeZone">The zone used to compute each record's local calendar day (PAR-006).
    /// Defaults to <see cref="TimeZoneInfo.Local"/>; tests inject a fixed zone so day-boundary
    /// behavior does not depend on the machine running the test.</param>
    public ClaudeTranscriptStore(string root, IClock clock, TimeZoneInfo? localTimeZone = null)
        : this([RequireNonBlankRoot(root)], clock, localTimeZone)
    {
    }

    /// <param name="roots">Every <c>projects</c> directory to scan (at least one). Roots that do not
    /// exist are skipped per refresh; data from all existing roots is combined and de-duplicated.</param>
    /// <param name="clock">Source of "now" for window queries and retention pruning.</param>
    /// <param name="localTimeZone">The zone used to compute each record's local calendar day.</param>
    public ClaudeTranscriptStore(IReadOnlyList<string> roots, IClock clock, TimeZoneInfo? localTimeZone = null)
        : this(roots, clock, localTimeZone, OpenRead, MaxSeenRecords)
    {
    }

    /// <summary>Test-only seam: lets a test substitute a controlled stream (e.g. one that cancels a
    /// token mid-read) in place of the real file open, to exercise cancellation deterministically.
    /// Production always uses <see cref="OpenRead"/>, preserving the single, real-file-only behavior.</summary>
    internal ClaudeTranscriptStore(
        string root, IClock clock, TimeZoneInfo? localTimeZone, Func<string, Stream> openFile)
        : this([RequireNonBlankRoot(root)], clock, localTimeZone, openFile, MaxSeenRecords)
    {
    }

    /// <summary>Keeps the single-root constructors' blank-input failure attributed to their own
    /// parameter (<c>root</c>) rather than the list constructor's (<c>roots</c>).</summary>
    private static string RequireNonBlankRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Root must not be blank.", nameof(root));
        }

        return root;
    }

    /// <summary>Test-only seam for the de-duplication set capacity (B7): production always uses
    /// <see cref="MaxSeenRecords"/>.</summary>
    internal ClaudeTranscriptStore(
        IReadOnlyList<string> roots, IClock clock, TimeZoneInfo? localTimeZone, int seenCapacity)
        : this(roots, clock, localTimeZone, OpenRead, seenCapacity)
    {
    }

    internal ClaudeTranscriptStore(
        IReadOnlyList<string> roots,
        IClock clock,
        TimeZoneInfo? localTimeZone,
        Func<string, Stream> openFile,
        int seenCapacity = MaxSeenRecords)
    {
        ArgumentNullException.ThrowIfNull(roots);

        if (roots.Count == 0)
        {
            throw new ArgumentException("At least one root is required.", nameof(roots));
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("Root must not be blank.", nameof(roots));
            }
        }

        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(openFile);

        if (seenCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seenCapacity), seenCapacity, "Seen-set capacity must be positive.");
        }

        _roots = [.. roots];
        _clock = clock;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _openFile = openFile;
        _seen = new SeenRecordSet(seenCapacity);
    }

    /// <summary>Lifetime totals across every record ever folded by this instance, never windowed or pruned.</summary>
    public UsageCounters AllTotals { get; private set; } = UsageCounters.Zero;

    /// <summary>How many <c>*.jsonl</c> files the most recent <see cref="RefreshAsync"/> found.</summary>
    public int FilesScanned { get; private set; }

    /// <summary>Records that looked like usage but could not be read (bad timestamp, bad token count, ...),
    /// counted rather than logged: a transcript line is the user's own conversation.</summary>
    public int MalformedRecordCount { get; private set; }

    /// <summary>Sanitized, actionable reason no transcripts directory could be scanned on the most recent
    /// <see cref="RefreshAsync"/> - none of the configured roots exists, or one could not be enumerated;
    /// <see langword="null"/> when the scan reached the per-file stage without a root-level failure.</summary>
    public string? LastDirectoryError { get; private set; }

    /// <summary>How many <c>*.jsonl</c> files the most recent <see cref="RefreshAsync"/> found but could
    /// not be stat'd or read (locked, permission denied, ...). Reset to zero at the start of every
    /// <see cref="RefreshAsync"/> call - unlike <see cref="MalformedRecordCount"/>, this is a per-refresh
    /// signal, not a lifetime one, since a transient lock on one refresh that clears up on the next
    /// should not linger forever. Counted rather than logged: the path itself is never exposed.</summary>
    public int FileReadFailureCount { get; private set; }

    /// <summary>
    /// Reads whatever has been appended to every <c>*.jsonl</c> file under every existing root since the
    /// last call, resuming each file from its own stored byte offset (the checkpoint). Never throws for
    /// an expected failure (missing directory, unreadable file, malformed line/record) - those are
    /// recorded on <see cref="LastDirectoryError"/>/<see cref="FileReadFailureCount"/>/
    /// <see cref="MalformedRecordCount"/> instead. The only exception a caller should expect is
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <returns>The number of new records folded into the counters.</returns>
    public async Task<int> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        LastDirectoryError = null;
        FileReadFailureCount = 0;

        var existingRoots = Array.FindAll(_roots, Directory.Exists);
        if (existingRoots.Length == 0)
        {
            LastDirectoryError = "No Claude transcripts directory found.";
            FilesScanned = 0;
            return 0;
        }

        var files = new List<(string Path, string Root)>();
        var enumerationFailed = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (var root in existingRoots)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", options))
                {
                    files.Add((path, root));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                enumerationFailed = true;
            }
        }

        if (enumerationFailed)
        {
            LastDirectoryError = "Could not read a Claude transcripts directory.";
        }

        files.Sort(static (left, right) => string.Compare(left.Path, right.Path, StringComparison.Ordinal));
        FilesScanned = files.Count;

        var added = 0;
        foreach (var (path, root) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            added += await ReadFileAsync(path, root, cancellationToken).ConfigureAwait(false);
        }

        if (added > 0)
        {
            Prune();
        }

        return added;
    }

    private async Task<int> ReadFileAsync(string path, string root, CancellationToken cancellationToken)
    {
        var offset = _offsets.GetValueOrDefault(path, 0L);

        long size;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A transcript being rotated or briefly locked right now can fail a stat; the next
            // refresh resumes from the same offset.
            FileReadFailureCount++;
            return 0;
        }

        if (size < offset)
        {
            offset = 0; // rotated or rewritten
        }

        if (size == offset)
        {
            return 0;
        }

        var (fallbackProject, fallbackSessionId) = ResolveFileFallback(path, root);
        var added = 0;
        var committedOffset = offset;

        try
        {
            using var stream = _openFile(path);
            stream.Seek(offset, SeekOrigin.Begin);

            using var pending = new MemoryStream();
            var buffer = new byte[ChunkSize];

            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                var start = 0;
                while (start < bytesRead)
                {
                    var newlineIndex = Array.IndexOf(buffer, (byte)'\n', start, bytesRead - start);
                    if (newlineIndex < 0)
                    {
                        pending.Write(buffer, start, bytesRead - start);
                        break;
                    }

                    var lengthIncludingNewline = newlineIndex - start + 1;
                    pending.Write(buffer, start, lengthIncludingNewline);

                    if (ProcessLine(
                            pending.GetBuffer().AsSpan(0, (int)pending.Length), fallbackProject, fallbackSessionId))
                    {
                        added++;
                    }

                    committedOffset += pending.Length;
                    pending.SetLength(0);
                    start = newlineIndex + 1;
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not re-thrown: a transcript being rotated or briefly locked right now is expected, and
            // the finally below already committed everything read before the failure. Still counted,
            // so a persistent lock is visible to the caller instead of silently disappearing.
            FileReadFailureCount++;
        }
        finally
        {
            // Runs on every exit from the try above - normal EOF, the handled I/O failure, and an
            // OperationCanceledException propagating through - so a checkpoint is never lost for a
            // line that was already folded into the counters. committedOffset only advances once a
            // complete line has been handed to ProcessLine in full (see the loop above), so a
            // partial trailing line is never committed early, and de-duplication for an id-less
            // record (which the seen-set cannot catch) no longer depends on this offset staying
            // put after a failed or cancelled read.
            _offsets[path] = committedOffset;
        }

        return added;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);

    /// <summary>Processes one complete line (already known to end in the newline that terminated it).
    /// Returns <see langword="true"/> only when it produced a newly-counted record.</summary>
    private bool ProcessLine(ReadOnlySpan<byte> rawLine, string fallbackProject, string fallbackSessionId)
    {
        var line = TrimAsciiWhitespace(rawLine);
        if (line.IsEmpty || line[0] != (byte)'{')
        {
            return false;
        }

        // Cheap pre-filter: only assistant records carry usage, and parsing every user/attachment
        // record is pure waste.
        if (!ContainsAssistantTypeMarker(line))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line.ToArray());
        }
        catch (JsonException)
        {
            // A transcript being written right now can be read mid-flush; the partial line will be
            // completed (or the whole line re-written) before this offset is revisited on a later
            // refresh. Not counted as malformed - that class is reserved for a record that parsed but
            // failed semantic validation.
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            try
            {
                return TryIngest(document.RootElement, fallbackProject, fallbackSessionId);
            }
            catch (Exception)
            {
                // TryIngest validates every field it uses before touching arithmetic, so this should
                // be unreachable. It exists because the alternative to being wrong about that is an
                // exception travelling out of the worker and blanking the whole Claude snapshot -
                // losing the server's quota gauges, which have nothing to do with local transcripts,
                // over one bad line (PAR-004).
                MalformedRecordCount++;
                return false;
            }
        }
    }

    /// <summary>
    /// Folds one already-parsed record into the counters, replacing an existing de-duplicated record
    /// when this one wins the survivor rules. Returns <see langword="true"/> only when a new message
    /// was counted - a replacement changes the counters but not the message count, so it returns
    /// <see langword="false"/>. Nothing is remembered about a record (no key is claimed) until every
    /// field it needs has been validated, so a record with one bad field never strands the id a
    /// corrected copy would need to count later.
    /// </summary>
    internal bool TryIngest(JsonElement record, string fallbackProject, string fallbackSessionId = "")
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!(TryGetString(record, "type", out var type) && type == "assistant"))
        {
            return false;
        }

        if (!record.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // P2b.3/B5: filtered before any key is claimed, and deliberately not counted as malformed.
        if (HasEmptyString(record, "sessionId") ||
            HasEmptyString(record, "requestId") ||
            HasEmptyString(message, "id") ||
            HasEmptyString(message, "model") ||
            HasUnsupportedVersion(record))
        {
            return false;
        }

        if (!TryGetString(record, "timestamp", out var timestampText))
        {
            MalformedRecordCount++;
            return false;
        }

        if (!DateTimeOffset.TryParse(
                timestampText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var whenUtc))
        {
            MalformedRecordCount++;
            return false;
        }

        if (!TryReadUsage(usage, out var tokens))
        {
            MalformedRecordCount++;
            return false;
        }

        var model = TryGetString(message, "model", out var modelValue) ? modelValue : "";
        var (cost, unpriced, estimated, modelLabel) = Price(model, tokens.Total, tokens.Input, tokens.Output,
            tokens.Write5m, tokens.Write1h, tokens.Read);
        modelLabel = InternLabel(_modelLabels, modelLabel);

        var project = ResolveProject(record, fallbackProject);
        var projectLabel = InternLabel(
            _projectLabels, string.IsNullOrEmpty(project) ? UnknownProjectLabel : project);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(whenUtc, _localTimeZone).DateTime);

        // Safe unchecked: a subset sum of the same non-negative fields already proven (above) to fit
        // in a long cannot itself overflow.
        var counters = UsageCounters.ForMessage(
            tokens.Input, tokens.Output, tokens.Write5m + tokens.Write1h, tokens.Read, cost, unpriced, estimated);

        var requestId = TryGetNonEmptyString(record, "requestId", out var requestIdValue) ? requestIdValue : null;
        var sessionId = TryGetNonEmptyString(record, "sessionId", out var sessionIdValue)
            ? sessionIdValue
            : fallbackSessionId;
        var isSidechain = IsSidechain(record);

        string? messageId = TryGetNonEmptyString(message, "id", out var messageIdValue) ? messageIdValue : null;
        var counted = false;
        if (messageId is null)
        {
            // No id: nothing can ever de-duplicate this record, so it is always counted. Offsets still
            // prevent re-reading it.
            Fold(day, modelLabel, projectLabel, counters);
            counted = true;
        }
        else
        {
            counted = CountRecord(
                day, modelLabel, projectLabel, counters, messageId, requestId, sessionId, isSidechain,
                timestampText, tokens.Total, cost);
        }

        // B16: advisor iterations are extra usage entries with their own model and token counts;
        // each is counted and de-duplicated exactly like a transcript record.
        counted |= CountAdvisorIterations(
            usage, messageId, requestId, sessionId, isSidechain, timestampText, day, projectLabel);

        return counted;
    }

    /// <summary>Counts one record (or advisor iteration) through the de-duplication rules. Returns
    /// <see langword="true"/> only when it added a new message rather than replacing or losing to an
    /// existing one.</summary>
    private bool CountRecord(
        DateOnly day,
        string modelLabel,
        string projectLabel,
        UsageCounters counters,
        string messageId,
        string? requestId,
        string sessionId,
        bool isSidechain,
        string timestampText,
        long totalTokens,
        decimal costUsd)
    {
        var exactKey = requestId is not null
            ? messageId + "\n" + requestId
            : IsAnthropicMessageId(messageId)
                ? messageId + "\n\n" + sessionId
                : messageId + "\n\n" + sessionId + "\n" + timestampText;
        var replayKey = messageId + "\n" + sessionId;

        // B12: only the 64-bit hashes are retained, never the key text.
        var exactHash = HashKey(exactKey);
        var replayHash = HashKey(replayKey);

        var existing = _seen.FindExact(exactHash) ?? _seen.FindReplay(replayHash, isSidechain);
        if (existing is null)
        {
            var kept = new SeenRecord(
                exactHash, replayHash, day, modelLabel, projectLabel, counters, isSidechain, costUsd);
            _seen.Add(kept);
            Fold(day, modelLabel, projectLabel, counters);
            return true;
        }

        // B1: a match that joins two sessions leaves the survivor reachable from both sessions' replay
        // keys - whether the incoming record wins or is discarded - so a later replay from either
        // session still finds it. A same-session match is a no-op here.
        _seen.AddReplayAlias(existing, replayHash);

        if (!ShouldReplace(existing, isSidechain, totalTokens, costUsd))
        {
            return false;
        }

        // The old record's contribution is withdrawn before the replacement is folded. A pruned day
        // bucket only makes the per-day subtraction a no-op; lifetime totals are always adjusted (B4).
        Unfold(existing);

        var replacement = new SeenRecord(
            exactHash, replayHash, day, modelLabel, projectLabel, counters, isSidechain, costUsd);

        // B1: a match that joins two sessions leaves the survivor reachable from either session's
        // replay key, so a later replay of the same response from either session still finds it.
        for (var index = 0; index < existing.ReplayKeyCount; index++)
        {
            replacement.AddReplayKey(existing.ReplayKeyAt(index));
        }

        _seen.Replace(existing, replacement);
        Fold(day, modelLabel, projectLabel, counters);
        return false;
    }

    /// <summary>B16: one extra record per <c>advisor_message</c> iteration with a non-empty model.
    /// The iteration inherits the parent's timestamp, session, request, sidechain flag, project and
    /// day; only its model and token counts differ. An iteration that fails validation is skipped
    /// alone, without invalidating the parent.</summary>
    private bool CountAdvisorIterations(
        JsonElement usage,
        string? parentMessageId,
        string? requestId,
        string sessionId,
        bool isSidechain,
        string timestampText,
        DateOnly day,
        string projectLabel)
    {
        if (!usage.TryGetProperty("iterations", out var iterations) || iterations.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var counted = false;
        var advisorIndex = 0;
        foreach (var iteration in iterations.EnumerateArray())
        {
            if (iteration.ValueKind != JsonValueKind.Object ||
                !TryGetString(iteration, "type", out var kind) ||
                kind != "advisor_message" ||
                !TryGetNonEmptyString(iteration, "model", out var advisorModel))
            {
                continue;
            }

            var index = advisorIndex++;
            if (!TryReadUsage(iteration, out var advisorTokens))
            {
                continue;
            }

            var (cost, unpriced, estimated, modelLabel) = Price(
                advisorModel, advisorTokens.Total, advisorTokens.Input, advisorTokens.Output,
                advisorTokens.Write5m, advisorTokens.Write1h, advisorTokens.Read);
            modelLabel = InternLabel(_modelLabels, modelLabel);

            var counters = UsageCounters.ForMessage(
                advisorTokens.Input,
                advisorTokens.Output,
                advisorTokens.Write5m + advisorTokens.Write1h,
                advisorTokens.Read,
                cost,
                unpriced,
                estimated);

            if (parentMessageId is null)
            {
                Fold(day, modelLabel, projectLabel, counters);
                counted = true;
                continue;
            }

            var advisorId = parentMessageId + ":advisor:" + index.ToString(CultureInfo.InvariantCulture);
            counted |= CountRecord(
                day, modelLabel, projectLabel, counters, advisorId, requestId, sessionId, isSidechain,
                timestampText, advisorTokens.Total, cost);
        }

        return counted;
    }

    /// <summary>Reads and validates the token fields shared by a transcript record's
    /// <c>usage</c> object and an advisor iteration. A field that is not a non-negative integer
    /// fails the whole read; a missing field is zero.</summary>
    private static bool TryReadUsage(JsonElement usage, out TokenUsage tokens)
    {
        long writeFiveMinute;
        long writeOneHour;
        if (usage.TryGetProperty("cache_creation", out var cacheCreation) && cacheCreation.ValueKind == JsonValueKind.Object)
        {
            var write5 = ReadTokenCount(cacheCreation, "ephemeral_5m_input_tokens");
            var write1 = ReadTokenCount(cacheCreation, "ephemeral_1h_input_tokens");
            if (write5 is null || write1 is null)
            {
                tokens = default;
                return false;
            }

            writeFiveMinute = write5.Value;
            writeOneHour = write1.Value;
        }
        else
        {
            // Older transcripts report only the aggregate; treat it as a 5-minute write.
            var write5 = ReadTokenCount(usage, "cache_creation_input_tokens");
            if (write5 is null)
            {
                tokens = default;
                return false;
            }

            writeFiveMinute = write5.Value;
            writeOneHour = 0;
        }

        var inputTokens = ReadTokenCount(usage, "input_tokens");
        var outputTokens = ReadTokenCount(usage, "output_tokens");
        var cacheRead = ReadTokenCount(usage, "cache_read_input_tokens");
        if (inputTokens is null || outputTokens is null || cacheRead is null)
        {
            tokens = default;
            return false;
        }

        // Validated - including overflow - before any key is claimed: every field here is
        // individually non-negative, but their sum can still exceed long.MaxValue, and an unchecked
        // sum would silently wrap into a bogus (typically negative) total instead of failing loudly.
        long total;
        try
        {
            total = checked(inputTokens.Value + outputTokens.Value + writeFiveMinute + writeOneHour + cacheRead.Value);
        }
        catch (OverflowException)
        {
            tokens = default;
            return false;
        }

        tokens = new TokenUsage(
            inputTokens.Value, outputTokens.Value, writeFiveMinute, writeOneHour, cacheRead.Value, total);
        return true;
    }

    private readonly record struct TokenUsage(
        long Input, long Output, long Write5m, long Write1h, long Read, long Total);

    /// <summary>B12: FNV-1a 64 over the key's UTF-16 code units. Only the hash is retained.</summary>
    internal static ulong HashKey(string key)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        foreach (var character in key)
        {
            hash ^= character;
            hash *= prime;
        }

        return hash;
    }

    private static string InternLabel(Dictionary<string, string> pool, string label)
    {
        if (pool.TryGetValue(label, out var shared))
        {
            return shared;
        }

        pool[label] = label;
        return label;
    }

    private static (decimal Cost, long Unpriced, long Estimated, string ModelLabel) Price(
        string model,
        long totalTokens,
        long inputTokens,
        long outputTokens,
        long cacheWrite5mTokens,
        long cacheWrite1hTokens,
        long cacheReadTokens)
    {
        if (model == SyntheticModelId)
        {
            // B3: ccusage counts synthetic usage, but it has no price and is neither "unpriced" nor
            // "estimated" (there is nothing to disclose as unknown or guessed).
            return (0m, 0, 0, SyntheticModelLabel);
        }

        var cost = ClaudeModelPricing.Cost(
            model, inputTokens, outputTokens, cacheWrite5mTokens, cacheWrite1hTokens, cacheReadTokens);
        var kind = ClaudeModelPricing.PriceKind(model);
        var unpriced = kind == PricingKind.Unknown ? totalTokens : 0;
        var estimated = kind == PricingKind.Estimated ? totalTokens : 0;
        return (cost, unpriced, estimated, ClaudeModelPricing.DisplayName(model));
    }

    private static bool ShouldReplace(SeenRecord existing, bool candidateIsSidechain, long candidateTotal, decimal candidateCost)
    {
        if (existing.IsSidechain != candidateIsSidechain)
        {
            return existing.IsSidechain;
        }

        if (existing.TotalTokens != candidateTotal)
        {
            return candidateTotal > existing.TotalTokens;
        }

        if (existing.CostUsd != candidateCost)
        {
            return candidateCost > existing.CostUsd;
        }

        return false;
    }

    private void Fold(DateOnly day, string modelLabel, string projectLabel, UsageCounters counters)
    {
        Fold(_byDayModel, day, modelLabel, counters);
        Fold(_byDayProject, day, projectLabel, counters);
        AllTotals = AllTotals.Combine(counters);
    }

    private void Unfold(SeenRecord record)
    {
        // B13: per-day tables are adjusted first, each field floored at zero (a bucket can have
        // been recreated after pruning with less than this record's contribution), then the exact
        // lifetime totals are adjusted last.
        UnfoldLabel(_byDayModel, record.Day, record.ModelLabel, record.Counters);
        UnfoldLabel(_byDayProject, record.Day, record.ProjectLabel, record.Counters);
        AllTotals = AllTotals.Subtract(record.Counters);
    }

    private static void Fold(
        Dictionary<DateOnly, Dictionary<string, UsageCounters>> table, DateOnly day, string label, UsageCounters counters)
    {
        if (!table.TryGetValue(day, out var perLabel))
        {
            perLabel = new Dictionary<string, UsageCounters>(StringComparer.Ordinal);
            table[day] = perLabel;
        }

        perLabel[label] = perLabel.TryGetValue(label, out var existing) ? existing.Combine(counters) : counters;
    }

    private static void UnfoldLabel(
        Dictionary<DateOnly, Dictionary<string, UsageCounters>> table,
        DateOnly day,
        string label,
        UsageCounters counters)
    {
        if (!table.TryGetValue(day, out var perLabel) || !perLabel.TryGetValue(label, out var existing))
        {
            return; // a pruned day bucket is not an error (B4)
        }

        var updated = SubtractFloored(existing, counters);
        if (updated.Messages == 0 && IsAllZero(updated))
        {
            perLabel.Remove(label);
            if (perLabel.Count == 0)
            {
                table.Remove(day);
            }

            return;
        }

        perLabel[label] = updated;
    }

    /// <summary>B13: per-field subtraction that floors at zero instead of throwing. Only the
    /// per-day tables use this; <see cref="AllTotals"/> is always subtracted exactly.</summary>
    private static UsageCounters SubtractFloored(UsageCounters current, UsageCounters other) =>
        new(
            Math.Max(0, current.Messages - other.Messages),
            Math.Max(0L, current.InputTokens - other.InputTokens),
            Math.Max(0L, current.OutputTokens - other.OutputTokens),
            Math.Max(0L, current.CacheWriteTokens - other.CacheWriteTokens),
            Math.Max(0L, current.CacheReadTokens - other.CacheReadTokens),
            current.CostUsd < other.CostUsd ? 0m : current.CostUsd - other.CostUsd,
            Math.Max(0L, current.UnpricedTokens - other.UnpricedTokens),
            Math.Max(0L, current.EstimatedTokens - other.EstimatedTokens));

    private static bool IsAllZero(UsageCounters counters) =>
        counters.InputTokens == 0 &&
        counters.OutputTokens == 0 &&
        counters.CacheWriteTokens == 0 &&
        counters.CacheReadTokens == 0 &&
        counters.CostUsd == 0m &&
        counters.UnpricedTokens == 0 &&
        counters.EstimatedTokens == 0;

    private void Prune()
    {
        var today = LocalToday();
        var cutoff = today.AddDays(-RetainedDays);
        PruneTable(_byDayModel, cutoff);
        PruneTable(_byDayProject, cutoff);
    }

    private static void PruneTable(Dictionary<DateOnly, Dictionary<string, UsageCounters>> table, DateOnly cutoff)
    {
        foreach (var day in table.Keys.Where(day => day < cutoff).ToArray())
        {
            table.Remove(day);
        }
    }

    // -- queries --------------------------------------------------------------------------------

    public UsageCounters WindowTotals(int days)
    {
        RequirePositive(days, nameof(days));

        var totals = UsageCounters.Zero;
        foreach (var day in DaysInRange(days))
        {
            if (_byDayModel.TryGetValue(day, out var perModel))
            {
                foreach (var counters in perModel.Values)
                {
                    totals = totals.Combine(counters);
                }
            }
        }

        return totals;
    }

    /// <summary>Models priced at a family rate inside this range, alphabetically.</summary>
    public IReadOnlyList<string> EstimatedInRange(int days) => ModelsInRange(days, static c => c.EstimatedTokens > 0);

    /// <summary>Models with unpriced tokens inside this range, alphabetically. Derived from the same
    /// per-day counters <see cref="WindowTotals"/> sums, so the names and the token figure always
    /// describe one range.</summary>
    public IReadOnlyList<string> UnpricedInRange(int days) => ModelsInRange(days, static c => c.UnpricedTokens > 0);

    private IReadOnlyList<string> ModelsInRange(int days, Func<UsageCounters, bool> predicate)
    {
        RequirePositive(days, nameof(days));

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var day in DaysInRange(days))
        {
            if (!_byDayModel.TryGetValue(day, out var perModel))
            {
                continue;
            }

            foreach (var (model, counters) in perModel)
            {
                if (predicate(counters))
                {
                    names.Add(model);
                }
            }
        }

        return [.. names];
    }

    /// <summary>Descending (label, value) pairs grouped by <c>"model"</c> or <c>"project"</c>; ties break
    /// on label for a deterministic order.</summary>
    public IReadOnlyList<UsageHistoryBreakdown> Breakdown(string key, string metric, int days)
    {
        RequirePositive(days, nameof(days));

        var table = key == "model" ? _byDayModel : _byDayProject;
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var day in DaysInRange(days))
        {
            if (!table.TryGetValue(day, out var perLabel))
            {
                continue;
            }

            foreach (var (label, counters) in perLabel)
            {
                var value = UsageMetric.ValueOf(counters, metric);
                if (value > 0)
                {
                    totals[label] = totals.GetValueOrDefault(label) + value;
                }
            }
        }

        return [.. totals
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new UsageHistoryBreakdown(pair.Key, pair.Value))];
    }

    /// <summary>Builds the chart-ready, provider-neutral <see cref="UsageHistory"/> for
    /// <paramref name="days"/>/<paramref name="metric"/>: per-day buckets with the largest contributor
    /// first (models past <see cref="MaxChartedModels"/> fold into "Other"), plus the by-model and
    /// by-project breakdowns for the same range.</summary>
    public UsageHistory BuildHistory(int days, string metric)
    {
        RequirePositive(days, nameof(days));
        var effectiveMetric = string.IsNullOrEmpty(metric) ? UsageMetric.TotalTokens : metric;

        var daysInRange = DaysInRange(days).ToArray();
        var perDayValues = new Dictionary<DateOnly, Dictionary<string, double>>();
        var totalsByModel = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var day in daysInRange)
        {
            var bucketValues = new Dictionary<string, double>(StringComparer.Ordinal);
            if (_byDayModel.TryGetValue(day, out var perModel))
            {
                foreach (var (model, counters) in perModel)
                {
                    var value = UsageMetric.ValueOf(counters, effectiveMetric);
                    if (value > 0)
                    {
                        bucketValues[model] = value;
                        totalsByModel[model] = totalsByModel.GetValueOrDefault(model) + value;
                    }
                }
            }

            perDayValues[day] = bucketValues;
        }

        var order = totalsByModel.Keys
            .OrderByDescending(model => totalsByModel[model])
            .ThenBy(model => model, StringComparer.Ordinal)
            .ToList();

        if (order.Count > MaxChartedModels)
        {
            var kept = order.Take(MaxChartedModels - 1).ToArray();
            var folded = order.Skip(MaxChartedModels - 1).ToHashSet(StringComparer.Ordinal);

            foreach (var day in daysInRange)
            {
                var bucketValues = perDayValues[day];
                var spill = 0.0;
                foreach (var name in folded)
                {
                    if (bucketValues.Remove(name, out var value))
                    {
                        spill += value;
                    }
                }

                if (spill > 0)
                {
                    bucketValues[OtherModelLabel] = spill;
                }
            }

            order = [.. kept, OtherModelLabel];
        }

        var buckets = daysInRange.Select(day => new UsageHistoryBucket(day, perDayValues[day])).ToArray();

        return new UsageHistory(
            buckets: buckets,
            series: order,
            byModel: Breakdown("model", effectiveMetric, days),
            byProject: Breakdown("project", effectiveMetric, days),
            days: days,
            metric: effectiveMetric);
    }

    private DateOnly LocalToday() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.UtcNow, _localTimeZone).DateTime);

    private IEnumerable<DateOnly> DaysInRange(int days)
    {
        var today = LocalToday();
        for (var offset = days - 1; offset >= 0; offset--)
        {
            yield return today.AddDays(-offset);
        }
    }

    private static void RequirePositive(int value, string paramName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be positive.");
        }
    }

    // -- record-shape helpers ---------------------------------------------------------------------

    private static (string Project, string SessionId) ResolveFileFallback(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var project = segments.Length > 1 ? ProjectFromDirectoryName(segments[0]) : "";
        var sessionId = segments.Length switch
        {
            0 => "",
            1 => Path.GetFileNameWithoutExtension(segments[0]),
            2 => Path.GetFileNameWithoutExtension(segments[1]),
            _ => segments[1], // the directory segment directly below the encoded project folder (B2)
        };

        return (project, sessionId);
    }

    private static bool IsAnthropicMessageId(string messageId) =>
        messageId.StartsWith("msg_", StringComparison.Ordinal) ||
        messageId.StartsWith("resp_", StringComparison.Ordinal);

    private static bool IsSidechain(JsonElement record) =>
        record.TryGetProperty("isSidechain", out var element) && element.ValueKind == JsonValueKind.True;

    private static bool HasEmptyString(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var element) &&
        element.ValueKind == JsonValueKind.String &&
        element.GetString() is { Length: 0 };

    /// <summary>B5: ccusage refuses a record whose <c>version</c> is present but is not a semver
    /// prefix (<c>digits.digits.digit…</c>). A null/absent version is accepted.</summary>
    private static bool HasUnsupportedVersion(JsonElement record)
    {
        if (!record.TryGetProperty("version", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        return element.ValueKind != JsonValueKind.String || !IsSemverPrefix(element.GetString() ?? "");
    }

    private static bool IsSemverPrefix(string value)
    {
        var index = 0;
        if (!ConsumeAsciiDigits(value, ref index) || index >= value.Length || value[index] != '.')
        {
            return false;
        }

        index++;
        if (!ConsumeAsciiDigits(value, ref index) || index >= value.Length || value[index] != '.')
        {
            return false;
        }

        index++;
        return index < value.Length && char.IsAsciiDigit(value[index]);
    }

    private static bool ConsumeAsciiDigits(string value, ref int index)
    {
        var start = index;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            index++;
        }

        return index > start;
    }

    private static string ResolveProject(JsonElement record, string fallbackProject)
    {
        if (!TryGetNonEmptyString(record, "cwd", out var cwd))
        {
            return fallbackProject;
        }

        // Both separators, on every OS: Path.GetFileName splits only on '/' outside Windows, so a
        // transcript recorded on Windows (C:\work\app) would otherwise be labelled with its whole path.
        var trimmed = cwd.TrimEnd('\\', '/');
        return trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..];
    }

    /// <summary>Best-effort project label when a record carries no <c>cwd</c>. The directory encoding
    /// is lossy - a hyphen inside a real folder name is indistinguishable from a separator - so this
    /// takes the trailing segment.</summary>
    private static string ProjectFromDirectoryName(string name)
    {
        var tail = name.Length > 3 && name[1] == '-' && name[2] == '-' ? name[3..] : name;
        var lastHyphen = tail.LastIndexOf('-');
        var candidate = lastHyphen >= 0 ? tail[(lastHyphen + 1)..] : tail;
        return string.IsNullOrEmpty(candidate) ? name : candidate;
    }

    private static bool TryGetString(JsonElement obj, string propertyName, out string value)
    {
        if (obj.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? "";
            return true;
        }

        value = "";
        return false;
    }

    private static bool TryGetNonEmptyString(JsonElement obj, string propertyName, out string value)
    {
        if (TryGetString(obj, propertyName, out var text) && text.Length > 0)
        {
            value = text;
            return true;
        }

        value = "";
        return false;
    }

    private static long? ReadTokenCount(JsonElement obj, string propertyName) =>
        ParseTokenCount(obj.TryGetProperty(propertyName, out var value) ? value : null);

    /// <summary>
    /// A token count from a transcript field, or <see langword="null"/> when it is not one. Missing is
    /// zero: transcripts omit fields that did not apply. Everything else has to prove itself, because
    /// these values are fed straight into arithmetic and a bad one must never raise out of the whole
    /// refresh. A JSON <c>true</c>/<c>false</c> is rejected even though some writers might coerce it -
    /// a token count of "true" means the writer was confused, not that one token was used - and so is
    /// anything negative or fractional.
    /// </summary>
    internal static long? ParseTokenCount(JsonElement? value)
    {
        if (value is not JsonElement element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return 0;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
            case JsonValueKind.False:
                return null;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    return integer >= 0 ? integer : null;
                }

                if (element.TryGetDouble(out var real) && !double.IsNaN(real) && !double.IsInfinity(real))
                {
                    return real >= 0 && real == Math.Floor(real) ? (long)real : null;
                }

                return null;

            case JsonValueKind.String:
                var text = element.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return 0;
                }

                return long.TryParse(
                    text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    && parsed >= 0
                    ? parsed
                    : null;

            default:
                return null;
        }
    }

    private static bool ContainsAssistantTypeMarker(ReadOnlySpan<byte> line) =>
        line.IndexOf(AssistantMarkerTight) >= 0 || line.IndexOf(AssistantMarkerSpaced) >= 0;

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && IsAsciiWhitespace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsAsciiWhitespace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsAsciiWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    /// <summary>One kept transcript record: its indexing hashes and exactly the contribution that
    /// was folded into the counters, so a replacement can withdraw it again. B12 keeps the keys as
    /// 64-bit hashes and the aliases in a small array (never a per-record set); the counters are
    /// stored as fields so one record does not also allocate a <see cref="UsageCounters"/>.</summary>
    private sealed class SeenRecord(
        ulong exactKeyHash,
        ulong replayKeyHash,
        DateOnly day,
        string modelLabel,
        string projectLabel,
        UsageCounters counters,
        bool isSidechain,
        decimal costUsd)
    {
        private ulong[] _aliases = [];
        private int _aliasCount;

        public ulong ExactKeyHash { get; } = exactKeyHash;

        public ulong ReplayKeyHash { get; } = replayKeyHash;

        public DateOnly Day { get; } = day;

        public string ModelLabel { get; } = modelLabel;

        public string ProjectLabel { get; } = projectLabel;

        public bool IsSidechain { get; } = isSidechain;

        /// <summary>Derived, not stored: every component was validated non-negative and their sum
        /// was already proven to fit in a <see cref="long"/> before the record existed.</summary>
        public long TotalTokens => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;

        public decimal CostUsd { get; } = costUsd;

        public long InputTokens { get; } = counters.InputTokens;

        public long OutputTokens { get; } = counters.OutputTokens;

        public long CacheWriteTokens { get; } = counters.CacheWriteTokens;

        public long CacheReadTokens { get; } = counters.CacheReadTokens;

        public long UnpricedTokens { get; } = counters.UnpricedTokens;

        public long EstimatedTokens { get; } = counters.EstimatedTokens;

        public SeenRecord? Previous { get; set; }

        public SeenRecord? Next { get; set; }

        /// <summary>The primary replay hash plus every cross-session alias (B1).</summary>
        public int ReplayKeyCount => 1 + _aliasCount;

        public UsageCounters Counters => new(
            messages: 1,
            inputTokens: InputTokens,
            outputTokens: OutputTokens,
            cacheWriteTokens: CacheWriteTokens,
            cacheReadTokens: CacheReadTokens,
            costUsd: CostUsd,
            unpricedTokens: UnpricedTokens,
            estimatedTokens: EstimatedTokens);

        public ulong ReplayKeyAt(int index) => index == 0 ? ReplayKeyHash : _aliases[index - 1];

        /// <summary>Adds a replay route, returning false when it is already primary or an alias.</summary>
        public bool AddReplayKey(ulong key)
        {
            if (key == ReplayKeyHash)
            {
                return false;
            }

            for (var index = 0; index < _aliasCount; index++)
            {
                if (_aliases[index] == key)
                {
                    return false;
                }
            }

            if (_aliasCount == _aliases.Length)
            {
                Array.Resize(ref _aliases, _aliases.Length == 0 ? 2 : _aliases.Length * 2);
            }

            _aliases[_aliasCount++] = key;
            return true;
        }
    }

    /// <summary>Insertion-ordered record set bounded to a fixed number of kept records. Evicting the
    /// oldest record removes its exact/replay hashes with it, so a later duplicate of an evicted
    /// record simply counts again - an accepted memory bound, not a correctness claim (B7). A record
    /// can be reachable from several replay hashes after a cross-session match (B1). B12: keys are
    /// 64-bit hashes (a hash hit is a match), insertion order is a pair of fields on the record
    /// instead of a linked-list node, and replay routes normally hold one record without a list.</summary>
    private sealed class SeenRecordSet(int capacity)
    {
        private readonly Dictionary<ulong, SeenRecord> _byExactKey = [];
        private readonly Dictionary<ulong, object> _byReplayKey = [];
        private SeenRecord? _head;
        private SeenRecord? _tail;
        private int _count;

        public SeenRecord? FindExact(ulong exactKeyHash) => _byExactKey.GetValueOrDefault(exactKeyHash);

        /// <summary>A replay match only joins two records when at least one of them is a sidechain
        /// record (the ccusage <c>/btw</c> replay rule).</summary>
        public SeenRecord? FindReplay(ulong replayKeyHash, bool candidateIsSidechain)
        {
            if (!_byReplayKey.TryGetValue(replayKeyHash, out var entry))
            {
                return null;
            }

            if (entry is SeenRecord single)
            {
                return candidateIsSidechain || single.IsSidechain ? single : null;
            }

            foreach (var record in (List<SeenRecord>)entry)
            {
                if (candidateIsSidechain || record.IsSidechain)
                {
                    return record;
                }
            }

            return null;
        }

        public void Add(SeenRecord record) => Register(record);

        /// <summary>Registers an additional replay route on an already-kept record (B1). Re-registering
        /// the same route is a no-op, so a same-session match changes nothing.</summary>
        public void AddReplayAlias(SeenRecord record, ulong replayKeyHash)
        {
            if (record.AddReplayKey(replayKeyHash))
            {
                RegisterReplayKey(record, replayKeyHash);
            }
        }

        public void Replace(SeenRecord existing, SeenRecord replacement)
        {
            Remove(existing);
            Register(replacement);
        }

        private void Register(SeenRecord record)
        {
            record.Previous = _tail;
            if (_tail is null)
            {
                _head = record;
            }
            else
            {
                _tail.Next = record;
            }

            _tail = record;
            _count++;

            _byExactKey[record.ExactKeyHash] = record;
            for (var index = 0; index < record.ReplayKeyCount; index++)
            {
                RegisterReplayKey(record, record.ReplayKeyAt(index));
            }

            while (_count > capacity)
            {
                Remove(_head!);
            }
        }

        private void RegisterReplayKey(SeenRecord record, ulong replayKeyHash)
        {
            if (!_byReplayKey.TryGetValue(replayKeyHash, out var entry))
            {
                _byReplayKey[replayKeyHash] = record;
                return;
            }

            if (entry is SeenRecord single)
            {
                if (ReferenceEquals(single, record))
                {
                    return;
                }

                _byReplayKey[replayKeyHash] = new List<SeenRecord>(2) { single, record };
                return;
            }

            var records = (List<SeenRecord>)entry;
            if (!records.Contains(record))
            {
                records.Add(record);
            }
        }

        private void Remove(SeenRecord record)
        {
            if (record.Previous is not null)
            {
                record.Previous.Next = record.Next;
            }
            else if (ReferenceEquals(_head, record))
            {
                _head = record.Next;
            }

            if (record.Next is not null)
            {
                record.Next.Previous = record.Previous;
            }
            else if (ReferenceEquals(_tail, record))
            {
                _tail = record.Previous;
            }

            record.Previous = null;
            record.Next = null;
            _count--;

            if (_byExactKey.TryGetValue(record.ExactKeyHash, out var exact) && ReferenceEquals(exact, record))
            {
                _byExactKey.Remove(record.ExactKeyHash);
            }

            for (var index = 0; index < record.ReplayKeyCount; index++)
            {
                RemoveReplayKey(record, record.ReplayKeyAt(index));
            }
        }

        private void RemoveReplayKey(SeenRecord record, ulong replayKeyHash)
        {
            if (!_byReplayKey.TryGetValue(replayKeyHash, out var entry))
            {
                return;
            }

            if (ReferenceEquals(entry, record))
            {
                _byReplayKey.Remove(replayKeyHash);
                return;
            }

            if (entry is not List<SeenRecord> records)
            {
                return;
            }

            records.Remove(record);
            if (records.Count == 0)
            {
                _byReplayKey.Remove(replayKeyHash);
            }
            else if (records.Count == 1)
            {
                _byReplayKey[replayKeyHash] = records[0];
            }
        }
    }
}
