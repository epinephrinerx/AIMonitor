namespace AIMonitor.Domain;

/// <summary>
/// Meter severity, ordered from least to most severe so callers can compare
/// instances directly (a higher value is always worse).
/// </summary>
public enum Severity
{
    Normal = 0,
    High = 1,
    VeryHigh = 2,
    Critical = 3,
}

/// <summary>Non-colour representation of a severity, and parsing of a server-reported label.</summary>
public static class SeverityLabels
{
    /// <summary>Glyph plus user-facing word, so colour never has to carry the state alone.</summary>
    public static (string Glyph, string Label) Describe(Severity severity) => severity switch
    {
        Severity.Normal => ("✓", "Normal"),
        Severity.High => ("⚠", "High"),
        Severity.VeryHigh => ("⚠", "Very high"),
        Severity.Critical => ("⚠", "Critical"),
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unknown severity."),
    };

    /// <summary>
    /// Maps a server-reported severity label to a known <see cref="Severity"/>.
    /// An absent or unrecognised label is treated as <see cref="Severity.Normal"/> rather than
    /// being allowed to outrank a known severity by accident.
    /// </summary>
    public static Severity Parse(string? serverSeverity) => serverSeverity?.Trim().ToLowerInvariant() switch
    {
        "warning" or "high" => Severity.High,
        "serious" or "very high" or "veryhigh" => Severity.VeryHigh,
        "critical" => Severity.Critical,
        _ => Severity.Normal,
    };
}
