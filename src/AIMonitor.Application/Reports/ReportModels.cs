using System.Globalization;
using System.Net;
using System.Text;
using AIMonitor.Domain;

namespace AIMonitor.Application.Reports;

public sealed record ReportRow(
    DateOnly Day,
    double Value,
    IReadOnlyDictionary<string, double>? PerModel = null)
{
    public IReadOnlyDictionary<string, double> PerModel { get; } =
        PerModel ?? new Dictionary<string, double>();
}

public sealed record ReportSection(
    string ProviderId,
    string ProviderName,
    string Account = "",
    string Source = "",
    string Status = "",
    string Note = "",
    IReadOnlyList<ReportRow>? Rows = null)
{
    public IReadOnlyList<ReportRow> Rows { get; } = Rows ?? [];

    public double Total => Rows.Sum(r => r.Value);

    public int ActiveDays => Rows.Count(r => r.Value > 0.0);
}

public sealed record UsageReport(
    DateTimeOffset GeneratedAt,
    string Metric,
    int Days,
    IReadOnlyList<ReportSection> Sections)
{
    public string ValueHeading => Metric;
}

public static class UsageReportGenerator
{
    public const string MoneyMetric = "Equivalent value";

    public static string FormatValue(double value, string metric)
    {
        if (string.Equals(metric, MoneyMetric, StringComparison.OrdinalIgnoreCase))
        {
            return $"${value.ToString("N2", CultureInfo.InvariantCulture)}";
        }

        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    public static string DefaultFilename(DateTimeOffset when, string extension = "csv")
    {
        var ext = extension.TrimStart('.');
        return $"ai-usage-log-{when.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{ext}";
    }

    public static UsageReport Build(
        IEnumerable<(string Id, string DisplayName)> providers,
        IReadOnlyDictionary<string, ProviderSnapshot> snapshots,
        int days,
        string metric,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(snapshots);

        var reportTime = generatedAt ?? DateTimeOffset.UtcNow;
        var sections = new List<ReportSection>();

        foreach (var (providerId, providerName) in providers)
        {
            if (!snapshots.TryGetValue(providerId, out var snapshot) || snapshot is null)
            {
                sections.Add(new ReportSection(
                    ProviderId: providerId,
                    ProviderName: providerName,
                    Note: "Not monitored."));
                continue;
            }

            var account = snapshot.Account;
            var source = snapshot.Detection?.SourceLabel ?? "";
            var status = snapshot.Detection is not null
                ? snapshot.Detection.State switch
                {
                    DetectionState.Connected => "Connected",
                    DetectionState.Limited => "Limited",
                    DetectionState.Expired => "Expired",
                    _ => "Not connected"
                }
                : (snapshot.Ok ? "Connected" : (snapshot.Error is not null ? "Error" : "Not connected"));

            var note = "";
            if (!string.IsNullOrEmpty(snapshot.Error))
            {
                note = snapshot.Error;
            }
            else if (!snapshot.Configured)
            {
                note = "Not connected.";
            }

            var rows = new List<ReportRow>();
            if (snapshot.History is not null && snapshot.History.Buckets.Count > 0)
            {
                foreach (var bucket in snapshot.History.Buckets)
                {
                    rows.Add(new ReportRow(bucket.Day, bucket.Total, bucket.PerModel));
                }

                rows.Sort((a, b) => a.Day.CompareTo(b.Day));
            }

            if (!string.IsNullOrEmpty(snapshot.HistoryError) && string.IsNullOrEmpty(note))
            {
                note = snapshot.HistoryError;
            }
            else if (rows.Count == 0 && string.IsNullOrEmpty(note))
            {
                note = "This service reports no daily history.";
            }

            sections.Add(new ReportSection(
                ProviderId: providerId,
                ProviderName: providerName,
                Account: account,
                Source: source,
                Status: status,
                Note: note,
                Rows: rows));
        }

        return new UsageReport(reportTime, metric, days, sections);
    }

    private static string FormatSubtitle(ReportSection section)
    {
        var parts = new[] { section.Account, section.Source, section.Status }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join("  ·  ", parts);
    }

    public static string ToMarkdown(UsageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        var when = report.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        sb.AppendLine("# AI Usage Monitor - usage log");
        sb.AppendLine();
        sb.AppendLine($"Generated {when}  ·  last {report.Days} days  ·  {report.Metric}");
        sb.AppendLine();

        foreach (var section in report.Sections)
        {
            sb.AppendLine($"## {section.ProviderName}");
            var subtitle = FormatSubtitle(section);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                sb.AppendLine();
                sb.AppendLine(subtitle);
            }

            if (!string.IsNullOrWhiteSpace(section.Note))
            {
                sb.AppendLine();
                sb.AppendLine($"_{section.Note}_");
            }

            if (section.Rows.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"| Date | {report.ValueHeading} |");
                sb.AppendLine("| --- | ---: |");
                foreach (var row in section.Rows)
                {
                    var val = FormatValue(row.Value, report.Metric);
                    var dateStr = row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    sb.AppendLine($"| {dateStr} | {val} |");
                }

                var total = FormatValue(section.Total, report.Metric);
                sb.AppendLine($"| **Total** | **{total}** |");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    public static string ToCsv(UsageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("date,provider,account,source,status,metric,value");

        var isMoney = string.Equals(report.Metric, MoneyMetric, StringComparison.OrdinalIgnoreCase);

        foreach (var section in report.Sections)
        {
            foreach (var row in section.Rows)
            {
                var formattedValue = isMoney
                    ? row.Value.ToString("F2", CultureInfo.InvariantCulture)
                    : row.Value.ToString("F0", CultureInfo.InvariantCulture);

                sb.Append(EscapeCsv(row.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append(',');
                sb.Append(EscapeCsv(section.ProviderName)).Append(',');
                sb.Append(EscapeCsv(section.Account)).Append(',');
                sb.Append(EscapeCsv(section.Source)).Append(',');
                sb.Append(EscapeCsv(section.Status)).Append(',');
                sb.Append(EscapeCsv(report.Metric)).Append(',');
                sb.AppendLine(formattedValue);
            }
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }
        return field;
    }

    public static string ToHtml(UsageReport report, bool dark = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        var ink = dark ? "#e8e8ea" : "#1b1b1f";
        var muted = dark ? "#9a9aa2" : "#63636b";
        var rule = dark ? "#3a3a42" : "#d9d9e0";
        var cellStyle = $"border-top:1px solid {rule}; color:{ink}; padding:6px 8px;";
        var when = WebUtility.HtmlEncode(report.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        var sb = new StringBuilder();
        sb.AppendLine($"<body style='color:{ink}; font-family:Segoe UI, sans-serif; background:{(dark ? "#14161a" : "#ffffff")}; margin:20px;'>");
        sb.AppendLine("<h1 style='font-size:19px; margin:0 0 2px 0;'>AI Usage Monitor &mdash; usage log</h1>");
        sb.AppendLine($"<p style='color:{muted}; font-size:11px; margin:0 0 18px 0;'>Generated {when} &nbsp;&middot;&nbsp; last {report.Days} days &nbsp;&middot;&nbsp; {WebUtility.HtmlEncode(report.Metric)}</p>");

        foreach (var section in report.Sections)
        {
            sb.AppendLine($"<h2 style='font-size:15px; margin:18px 0 2px 0;'>{WebUtility.HtmlEncode(section.ProviderName)}</h2>");
            var subtitle = FormatSubtitle(section);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                sb.AppendLine($"<p style='color:{muted}; font-size:11px; margin:0 0 8px 0;'>{WebUtility.HtmlEncode(subtitle)}</p>");
            }

            if (!string.IsNullOrWhiteSpace(section.Note))
            {
                sb.AppendLine($"<p style='color:{muted}; font-size:12px; margin:0 0 8px 0;'><em>{WebUtility.HtmlEncode(section.Note)}</em></p>");
            }

            if (section.Rows.Count == 0) continue;

            sb.AppendLine($"<table cellspacing='0' cellpadding='4' width='100%' style='font-size:12px; border-top:1px solid {rule}; border-collapse:collapse; margin-bottom:16px;'>");
            sb.AppendLine($"<tr><th align='left' style='color:{muted}; padding:6px 8px;'>Date</th><th align='right' style='color:{muted}; padding:6px 8px;'>{WebUtility.HtmlEncode(report.ValueHeading)}</th></tr>");

            foreach (var row in section.Rows)
            {
                var dateStr = row.Day.ToString("yyyy-MM-dd (ddd)", CultureInfo.InvariantCulture);
                var valStr = FormatValue(row.Value, report.Metric);
                sb.AppendLine($"<tr><td style='{cellStyle}'>{WebUtility.HtmlEncode(dateStr)}</td><td align='right' style='{cellStyle}'>{WebUtility.HtmlEncode(valStr)}</td></tr>");
            }

            var totalStr = FormatValue(section.Total, report.Metric);
            sb.AppendLine($"<tr><td style='{cellStyle} font-weight:600;'>Total <span style='color:{muted}; font-weight:400;'>({section.ActiveDays} active of {section.Rows.Count} days)</span></td><td align='right' style='{cellStyle} font-weight:600;'>{WebUtility.HtmlEncode(totalStr)}</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine("</body>");
        return sb.ToString();
    }
}
