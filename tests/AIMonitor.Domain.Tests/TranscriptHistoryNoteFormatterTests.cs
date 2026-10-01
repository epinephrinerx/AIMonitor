using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

public class TranscriptHistoryNoteFormatterTests
{
    [Fact]
    public void Format_NoErrorAndNoMalformedRecords_ReturnsNull() =>
        Assert.Null(TranscriptHistoryNoteFormatter.Format(null, 0, 0));

    [Fact]
    public void Format_DirectoryError_ReturnsItVerbatim() =>
        Assert.Equal(
            "No transcripts directory at C:\\x",
            TranscriptHistoryNoteFormatter.Format("No transcripts directory at C:\\x", 0, 0));

    [Fact]
    public void Format_DirectoryErrorTakesPriorityOverMalformedCount() =>
        Assert.Equal("dir error", TranscriptHistoryNoteFormatter.Format("dir error", 5, 0));

    [Fact]
    public void Format_DirectoryErrorTakesPriorityOverFileReadFailureCount() =>
        Assert.Equal("dir error", TranscriptHistoryNoteFormatter.Format("dir error", 0, 5));

    [Fact]
    public void Format_MultipleMalformedRecords_UsesPluralWordingAndReassuresAboutGauges()
    {
        var note = TranscriptHistoryNoteFormatter.Format(null, 3, 0);

        Assert.Contains("3 transcript records", note);
        Assert.Contains("quota gauges are unaffected", note);
    }

    [Fact]
    public void Format_OneMalformedRecord_UsesSingularWording()
    {
        var note = TranscriptHistoryNoteFormatter.Format(null, 1, 0);
        Assert.Contains("1 transcript record could not", note);
    }

    [Fact]
    public void Format_NegativeMalformedCount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptHistoryNoteFormatter.Format(null, -1, 0));

    [Fact]
    public void Format_EmptyDirectoryError_FallsThroughToMalformedCount()
    {
        // An empty string is not a meaningful error message; treat it like "no directory error".
        var note = TranscriptHistoryNoteFormatter.Format("", 2, 0);
        Assert.Contains("2 transcript records", note);
    }

    [Fact]
    public void Format_MultipleFileReadFailures_UsesPluralWordingAndReassuresAboutGauges()
    {
        var note = TranscriptHistoryNoteFormatter.Format(null, 0, 4);

        Assert.Contains("4 transcript files", note);
        Assert.Contains("quota gauges are unaffected", note);
    }

    [Fact]
    public void Format_OneFileReadFailure_UsesSingularWording()
    {
        var note = TranscriptHistoryNoteFormatter.Format(null, 0, 1);
        Assert.Contains("1 transcript file could not", note);
    }

    [Fact]
    public void Format_NegativeFileReadFailureCount_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptHistoryNoteFormatter.Format(null, 0, -1));

    [Fact]
    public void Format_BothFileReadFailuresAndMalformedRecords_MentionsBothCountsAndReassuresAboutGauges()
    {
        var note = TranscriptHistoryNoteFormatter.Format(null, 2, 1);

        Assert.Contains("1 transcript file", note);
        Assert.Contains("2 transcript records", note);
        Assert.Contains("quota gauges are unaffected", note);
    }
}
