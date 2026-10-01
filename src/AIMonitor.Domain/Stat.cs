namespace AIMonitor.Domain;

/// <summary>A plain numeric readout that has no percentage or reset time of its own.</summary>
public sealed record Stat
{
    public string Label { get; }

    public string Value { get; }

    public string Detail { get; }

    public Stat(string label, string value, string detail = "")
    {
        Label = DomainGuard.RequireNonBlank(label, nameof(label));
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Detail = detail ?? string.Empty;
    }
}
