using System.Globalization;
using System.Text.Json;
using AIMonitor.Application.Time;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.Providers.Claude;

/// <summary>
/// Incrementally parsed, pre-aggregated view of the local Claude Code transcripts under one
/// <c>projects</c> directory (PAR-006). Read-only: never creates, modifies, or deletes a transcript
/// file. Aggregates on ingest - a record is folded into per-day/model/project counters and then
/// discarded, never retained - so memory is bounded by (days retained x models/projects), not by
/// history length. Every public member after construction is safe to call repeatedly across the
/// lifetime of one Claude provider client; the byte offset and de-duplication state recorded by
/// <see cref="RefreshAsync"/> are this store's explicit checkpoint; there is no separate synchronous
/// re-scan path.
/// </summary>
public sealed class ClaudeTranscriptStore
{
    /// <summary>Longest range the UI offers is 90 days; beyond this, day buckets are dropped.</summary>
    private const int RetainedDays = 400;

    /// <summary>Bounds the de-duplication set on a machine with very long history. Oldest ids are
    /// evicted first; an evicted id can only be re-counted if its transcript is re-read from offset zero
    /// (e.g. after rotation), matching <see cref="RefreshAsync"/>'s own rotation handling.</summary>
    private const int MaxSeenIds = 400_000;

    /// <summary>Models past this many fold into "Other" in a built <see cref="UsageHistory"/>, so a
    /// chart's categorical palette is never asked to render more series than it has colours for.</summary>
    private const int MaxChartedModels = 8;

    private const string OtherModelLabel = "Other";
    private const string UnknownProjectLabel = "(unknown)";
    private const int ChunkSize = 8192;

    private static readonly byte[] AssistantMarkerTight = "\"type\":\"assistant\""u8.ToArray();
    private static readonly byte[] AssistantMarkerSpaced = "\"type\": \"assistant\""u8.ToArray();

    private readonly string _root;
    private readonly IClock _clock;
    private readonly TimeZoneInfo _localTimeZone;
    private readonly Func<string, Stream> _openFile;
    private readonly Dictionary<string, long> _offsets = new(StringComparer.Ordinal);
    private readonly BoundedSeenIdSet _seenIds = new(MaxSeenIds);
    private readonly Dictionary<DateOnly, Dictionary<string, UsageCounters>> _byDayModel = [];
    private readonly Dictionary<DateOnly, Dictionary<string, UsageCounters>> _byDayProject = [];

    /// <param name="root">The <c>projects</c> directory to scan; see <see cref="ClaudeTranscriptPathResolver"/>.
    /// Does not need to exist yet - a missing directory is an expected, reported condition, not a
    /// construction-time error, since Claude Code may not have written any transcripts yet.</param>
    /// <param name="clock">Source of "now" for window queries and retention pruning.</param>
    /// <param name="localTimeZone">The zone used to compute each record's local calendar day (PAR-006).
    /// Defaults to <see cref="TimeZoneInfo.Local"/>; tests inject a fixed zone so day-boundary
    /// behavior does not depend on the machine running the test.</param>
    public ClaudeTranscriptStore(string root, IClock clock, TimeZoneInfo? localTimeZone = null)
        : this(root, clock, localTimeZone, OpenRead)
    {
    }

    /// <summary>Test-only seam: lets a test substitute a controlled stream (e.g. one that cancels a
    /// token mid-read) in place of the real file open, to exercise cancellation deterministically.
    /// Production always uses <see cref="OpenRead"/>, preserving the single, real-file-only behavior.</summary>
    internal ClaudeTranscriptStore(string root, IClock clock, TimeZoneInfo? localTimeZone, Func<string, Stream> openFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(openFile);

        _root = root;
        _clock = clock;
        _localTimeZone = localTimeZone ?? TimeZoneInfo.Local;
        _openFile = openFile;
    }

    /// <summary>Lifetime totals across every record ever folded by this instance, never windowed or pruned.</summary>
    public UsageCounters AllTotals { get; private set; } = UsageCounters.Zero;

    /// <summary>How many <c>*.jsonl</c> files the most recent <see cref="RefreshAsync"/> found.</summary>
    public int FilesScanned { get; private set; }

    /// <summary>Records that looked like usage but could not be read (bad timestamp, bad token count, ...),
    /// counted rather than logged: a transcript line is the user's own conversation.</summary>
    public int MalformedRecordCount { get; private set; }

    /// <summary>Sanitized, actionable reason the transcripts directory itself could not be scanned on the
    /// most recent <see cref="RefreshAsync"/>; <see langword="null"/> when the scan reached the per-file stage.</summary>
    public string? LastDirectoryError { get; private set; }

    /// <summary>How many <c>*.jsonl</c> files the most recent <see cref="RefreshAsync"/> found but could
    /// not be stat'd or read (locked, permission denied, ...). Reset to zero at the start of every
    /// <see cref="RefreshAsync"/> call - unlike <see cref="MalformedRecordCount"/>, this is a per-refresh
    /// signal, not a lifetime one, since a transient lock on one refresh that clears up on the next
    /// should not linger forever. Counted rather than logged: the path itself is never exposed.</summary>
    public int FileReadFailureCount { get; private set; }

    /// <summary>
    /// Reads whatever has been appended to every <c>*/*.jsonl</c> file under the root since the last
    /// call, resuming each file from its own stored byte offset (the checkpoint). Never throws for an
    /// expected failure (missing directory, unreadable file, malformed line/record) - those are
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

        if (!Directory.Exists(_root))
        {
            LastDirectoryError = $"No Claude transcripts directory found at {_root}.";
            FilesScanned = 0;
            return 0;
        }

        string[] files;
        try
        {
            files = [.. Directory.EnumerateDirectories(_root)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.jsonl"))
                .OrderBy(path => path, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastDirectoryError = $"Could not read the Claude transcripts directory at {_root}.";
            FilesScanned = 0;
            return 0;
        }

        FilesScanned = files.Length;

        var added = 0;
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            added += await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        }

        if (added > 0)
        {
            Prune();
        }

        return added;
    }

    private async Task<int> ReadFileAsync(string path, CancellationToken cancellationToken)
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

        var fallbackProject = ProjectFromDirectoryName(Path.GetFileName(Path.GetDirectoryName(path)) ?? "");
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

                    if (ProcessLine(pending.GetBuffer().AsSpan(0, (int)pending.Length), fallbackProject))
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
            // record (which BoundedSeenIdSet cannot catch) no longer depends on this offset staying
            // put after a failed or cancelled read.
            _offsets[path] = committedOffset;
        }

        return added;
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);

    /// <summary>Processes one complete line (already known to end in the newline that terminated it).
    /// Returns <see langword="true"/> only when it produced a newly-counted record.</summary>
    private bool ProcessLine(ReadOnlySpan<byte> rawLine, string fallbackProject)
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
                return TryIngest(document.RootElement, fallbackProject);
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
    /// Folds one already-parsed record into the counters. Returns <see langword="true"/> if it counted.
    /// Nothing is remembered about a record (its de-duplication id is not claimed) until every field it
    /// needs has been validated, so a record with one bad field never strands the id a corrected copy
    /// would need to count later.
    /// </summary>
    internal bool TryIngest(JsonElement record, string fallbackProject)
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

        var key = ResolveDedupKey(record, message);
        if (key is not null && _seenIds.Contains(key))
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

        long writeFiveMinute;
        long writeOneHour;
        if (usage.TryGetProperty("cache_creation", out var cacheCreation) && cacheCreation.ValueKind == JsonValueKind.Object)
        {
            var write5 = ReadTokenCount(cacheCreation, "ephemeral_5m_input_tokens");
            var write1 = ReadTokenCount(cacheCreation, "ephemeral_1h_input_tokens");
            if (write5 is null || write1 is null)
            {
                MalformedRecordCount++;
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
                MalformedRecordCount++;
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
            MalformedRecordCount++;
            return false;
        }

        // Validated - including overflow - before the id is claimed below: every field here is
        // individually non-negative, but their sum can still exceed long.MaxValue, and an unchecked
        // sum would silently wrap into a bogus (typically negative) total instead of failing loudly.
        long total;
        try
        {
            total = checked(inputTokens.Value + outputTokens.Value + writeFiveMinute + writeOneHour + cacheRead.Value);
        }
        catch (OverflowException)
        {
            MalformedRecordCount++;
            return false;
        }

        // Past this point the record is known good, so claiming its id cannot strand a record that
        // would otherwise have counted.
        if (key is not null)
        {
            _seenIds.Add(key);
        }

        var model = TryGetString(message, "model", out var modelValue) ? modelValue : "";
        var cost = ClaudeModelPricing.Cost(
            model, inputTokens.Value, outputTokens.Value, writeFiveMinute, writeOneHour, cacheRead.Value);
        var kind = ClaudeModelPricing.PriceKind(model);
        var unpriced = kind == PricingKind.Unknown ? total : 0;
        var estimated = kind == PricingKind.Estimated ? total : 0;

        var project = ResolveProject(record, fallbackProject);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(whenUtc, _localTimeZone).DateTime);
        var modelLabel = ClaudeModelPricing.DisplayName(model);

        // Safe unchecked: a subset sum of the same non-negative fields already proven (above) to fit
        // in a long cannot itself overflow.
        var counters = UsageCounters.ForMessage(
            inputTokens.Value, outputTokens.Value, writeFiveMinute + writeOneHour, cacheRead.Value, cost, unpriced, estimated);

        Fold(_byDayModel, day, modelLabel, counters);
        Fold(_byDayProject, day, string.IsNullOrEmpty(project) ? UnknownProjectLabel : project, counters);
        AllTotals = AllTotals.Combine(counters);

        return true;
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

    private static string? ResolveDedupKey(JsonElement record, JsonElement message)
    {
        if (TryGetNonEmptyString(message, "id", out var messageId))
        {
            return messageId;
        }

        return TryGetNonEmptyString(record, "requestId", out var requestId) ? requestId : null;
    }

    private static string ResolveProject(JsonElement record, string fallbackProject)
    {
        if (!TryGetNonEmptyString(record, "cwd", out var cwd))
        {
            return fallbackProject;
        }

        return Path.GetFileName(cwd.TrimEnd('\\', '/'));
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

    /// <summary>Insertion-ordered id set bounded to <see cref="MaxSeenIds"/>, evicting the oldest id
    /// first once full - mirrors an LRU-by-insertion policy without needing full LRU-by-access
    /// machinery, since a de-duplication set only ever needs "was this seen before", never "was this
    /// seen recently".</summary>
    private sealed class BoundedSeenIdSet(int capacity)
    {
        private readonly HashSet<string> _members = new(StringComparer.Ordinal);
        private readonly Queue<string> _insertionOrder = new();

        public bool Contains(string id) => _members.Contains(id);

        public void Add(string id)
        {
            if (!_members.Add(id))
            {
                return;
            }

            _insertionOrder.Enqueue(id);
            while (_members.Count > capacity)
            {
                _members.Remove(_insertionOrder.Dequeue());
            }
        }
    }
}
