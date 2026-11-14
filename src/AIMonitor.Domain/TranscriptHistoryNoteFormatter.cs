namespace AIMonitor.Domain;

/// <summary>
/// Formats a local-transcript ingest outcome into the text shown for
/// <see cref="ProviderSnapshot.HistoryError"/>. Quota percentages come from the provider's server;
/// local transcripts have no bearing on them, so this note is always reassuring about the gauges
/// when it names a problem, and is <see langword="null"/> - not an empty string - when there is
/// nothing to report.
/// </summary>
public static class TranscriptHistoryNoteFormatter
{
    /// <param name="directoryError">A sanitized, already-actionable description of why the transcripts
    /// directory itself could not be read (missing, inaccessible). Takes priority: if the directory
    /// could not be scanned at all, the per-file/per-record counts below are meaningless.</param>
    /// <param name="malformedRecordCount">How many individual records were skipped because they could
    /// not be validated (bad timestamp, bad token count, ...). Must not be negative.</param>
    /// <param name="fileReadFailureCount">How many transcript files themselves could not be stat'd or
    /// read (locked, permission denied, ...) on the most recent refresh. Must not be negative.</param>
    public static string? Format(
        string? directoryError,
        int malformedRecordCount,
        int fileReadFailureCount = 0)
    {
        if (!string.IsNullOrEmpty(directoryError))
        {
            return directoryError;
        }

        if (malformedRecordCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(malformedRecordCount), malformedRecordCount, "Value cannot be negative.");
        }

        if (fileReadFailureCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fileReadFailureCount), fileReadFailureCount, "Value cannot be negative.");
        }

        if (malformedRecordCount == 0 && fileReadFailureCount == 0)
        {
            return null;
        }

        var parts = new List<string>(capacity: 2);
        if (fileReadFailureCount > 0)
        {
            var noun = fileReadFailureCount == 1 ? "file" : "files";
            parts.Add($"{fileReadFailureCount} transcript {noun}");
        }

        if (malformedRecordCount > 0)
        {
            var noun = malformedRecordCount == 1 ? "record" : "records";
            parts.Add($"{malformedRecordCount} transcript {noun}");
        }

        return $"{string.Join(" and ", parts)} could not be read; quota gauges are unaffected.";
    }
}
