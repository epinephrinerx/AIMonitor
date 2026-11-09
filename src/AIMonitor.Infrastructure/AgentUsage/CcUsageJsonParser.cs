using System.Globalization;
using System.Text.Json;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Domain;

namespace AIMonitor.Infrastructure.AgentUsage;

/// <summary>
/// Parses the JSON emitted by <c>ccusage daily --json --by-agent --sections daily,monthly,session</c>.
/// </summary>
public static class CcUsageJsonParser
{
    private const int MaxRowsPerSection = 20_000;
    private const int MaxModelsPerRow = 500;
    private const long MaxTokenValue = (1L << 53) - 1;

    public static AgentUsageReport Parse(string json, AgentUsagePricing pricing, DateTimeOffset retrievedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 64 });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid();

            var dailyArray = RequireArray(root, "daily");
            var monthlyArray = RequireArray(root, "monthly");
            var sessionArray = RequireArray(root, "session");

            if (dailyArray.GetArrayLength() > MaxRowsPerSection
                || monthlyArray.GetArrayLength() > MaxRowsPerSection
                || sessionArray.GetArrayLength() > MaxRowsPerSection)
            {
                throw Invalid();
            }

            EnforceModelBreakdownLimit(dailyArray);
            EnforceModelBreakdownLimit(monthlyArray);

            var skipped = 0;
            var daily = new List<DailyAgentUsage>();
            var monthly = new List<MonthlyAgentUsage>();
            var sessions = new List<SessionAgentUsage>();

            foreach (var row in dailyArray.EnumerateArray())
            {
                if (TryParseDailyRow(row, out var usage, ref skipped))
                    daily.Add(usage);
            }

            foreach (var row in monthlyArray.EnumerateArray())
            {
                if (TryParseMonthlyRow(row, out var usage, ref skipped))
                    monthly.Add(usage);
            }

            foreach (var row in sessionArray.EnumerateArray())
            {
                if (TryParseSessionRow(row, out var usage, ref skipped))
                    sessions.Add(usage);
            }

            var totalRows = dailyArray.GetArrayLength() + monthlyArray.GetArrayLength() + sessionArray.GetArrayLength();
            if (skipped > 0 && skipped == totalRows)
                throw Invalid();

            daily.Sort((a, b) => a.Date.CompareTo(b.Date));
            monthly.Sort((a, b) =>
            {
                var yearComparison = a.Year.CompareTo(b.Year);
                return yearComparison != 0 ? yearComparison : a.Month.CompareTo(b.Month);
            });
            sessions.Sort((a, b) => b.LastActivity.CompareTo(a.LastActivity));

            var unpricedModels = ReadUnpricedModels(root);
            return new AgentUsageReport(daily, monthly, sessions, unpricedModels, pricing, retrievedAt, skipped);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
    }

    private static AgentUsageUnavailableException Invalid()
        => new("ccusage returned data in an unexpected format. Check ccusage output, then refresh.", AgentUsageErrorKind.InvalidResponse);

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array)
            throw Invalid();
        return property;
    }

    private static void EnforceModelBreakdownLimit(JsonElement section)
    {
        foreach (var row in section.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty("modelBreakdowns", out var models)) continue;
            if (models.ValueKind != JsonValueKind.Array) continue;
            if (models.GetArrayLength() > MaxModelsPerRow) throw Invalid();
        }
    }

    private static bool TryParseDailyRow(JsonElement row, out DailyAgentUsage usage, ref int skipped)
    {
        usage = null!;
        if (row.ValueKind != JsonValueKind.Object) { skipped++; return false; }

        if (!row.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.String) { skipped++; return false; }
        if (!DateOnly.TryParseExact(period.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { skipped++; return false; }

        if (!TryParseAgents(row, out var agents)) { skipped++; return false; }

        usage = new DailyAgentUsage(date, agents);
        return true;
    }

    private static bool TryParseMonthlyRow(JsonElement row, out MonthlyAgentUsage usage, ref int skipped)
    {
        usage = null!;
        if (row.ValueKind != JsonValueKind.Object) { skipped++; return false; }

        if (!row.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.String) { skipped++; return false; }
        if (!DateTime.TryParseExact(period.GetString(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) { skipped++; return false; }

        if (!TryParseAgents(row, out var agents)) { skipped++; return false; }

        usage = new MonthlyAgentUsage(dt.Year, dt.Month, agents);
        return true;
    }

    private static bool TryParseAgents(JsonElement row, out IReadOnlyList<Domain.AgentUsage> agents)
    {
        agents = null!;
        if (!row.TryGetProperty("agents", out var agentsProperty) || agentsProperty.ValueKind != JsonValueKind.Array)
            return false;

        var list = new List<Domain.AgentUsage>();
        foreach (var element in agentsProperty.EnumerateArray())
        {
            if (!TryParseAgentUsage(element, true, out var usage)) return false;
            list.Add(usage);
        }

        agents = list.AsReadOnly();
        return true;
    }

    private static bool TryParseSessionRow(JsonElement row, out SessionAgentUsage usage, ref int skipped)
    {
        usage = null!;
        if (row.ValueKind != JsonValueKind.Object) { skipped++; return false; }

        if (!row.TryGetProperty("period", out var period) || period.ValueKind != JsonValueKind.String) { skipped++; return false; }
        var sessionId = period.GetString();
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256) { skipped++; return false; }

        if (!row.TryGetProperty("agent", out var agentProperty) || agentProperty.ValueKind != JsonValueKind.String) { skipped++; return false; }
        var agentName = agentProperty.GetString() ?? string.Empty;

        if (!row.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object) { skipped++; return false; }
        if (!metadata.TryGetProperty("lastActivity", out var lastActivity) || lastActivity.ValueKind != JsonValueKind.String) { skipped++; return false; }
        if (!TryParseIsoOffset(lastActivity.GetString(), out var lastActivityOffset)) { skipped++; return false; }

        if (!TryParseAgentUsage(row, false, out var usageData)) { skipped++; return false; }

        usage = new SessionAgentUsage(
            sessionId,
            lastActivityOffset,
            new Domain.AgentUsage(agentName, usageData.Tokens, usageData.Models));
        return true;
    }

    private static bool TryParseAgentUsage(JsonElement element, bool allowModels, out Domain.AgentUsage usage)
    {
        usage = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (!TryParseAgentTokenCounts(element, out var tokens)) return false;

        string? agentId = null;
        if (element.TryGetProperty("agent", out var agentProperty) && agentProperty.ValueKind == JsonValueKind.String)
            agentId = agentProperty.GetString();

        if (string.IsNullOrWhiteSpace(agentId)) return false;

        var models = new List<ModelUsage>();
        if (allowModels && element.TryGetProperty("modelBreakdowns", out var modelsProperty) && modelsProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var modelElement in modelsProperty.EnumerateArray())
            {
                if (!TryParseModelUsage(modelElement, out var modelUsage)) return false;
                models.Add(modelUsage);
            }
        }

        usage = new Domain.AgentUsage(agentId, tokens, models);
        return true;
    }

    private static bool TryParseAgentTokenCounts(JsonElement element, out TokenCounts tokens)
    {
        tokens = null!;
        if (!TryGetTokenLong(element, "inputTokens", out var input)) return false;
        if (!TryGetTokenLong(element, "outputTokens", out var output)) return false;
        if (!TryGetTokenLong(element, "cacheCreationTokens", out var cacheCreation)) return false;
        if (!TryGetTokenLong(element, "cacheReadTokens", out var cacheRead)) return false;
        if (!TryGetTokenLong(element, "totalTokens", out var total)) return false;

        if (total < input + output + cacheCreation + cacheRead) return false;

        tokens = new TokenCounts(input, output, cacheCreation, cacheRead, total);
        return true;
    }

    private static bool TryParseModelUsage(JsonElement element, out ModelUsage usage)
    {
        usage = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;

        if (!element.TryGetProperty("modelName", out var nameProperty) || nameProperty.ValueKind != JsonValueKind.String) return false;
        var modelName = nameProperty.GetString();
        if (string.IsNullOrWhiteSpace(modelName)) return false;

        if (!TryParseModelTokenCounts(element, out var tokens)) return false;

        decimal? cost = null;
        var missingPricing = element.TryGetProperty("missingPricing", out var mp) && mp.ValueKind == JsonValueKind.True;

        if (!missingPricing)
        {
            if (element.TryGetProperty("cost", out var costProperty) && costProperty.ValueKind == JsonValueKind.Number)
            {
                if (!costProperty.TryGetDecimal(out var costValue) || costValue < 0) return false;
                cost = costValue;
            }
        }

        usage = new ModelUsage(modelName, tokens, cost);
        return true;
    }

    private static bool TryParseModelTokenCounts(JsonElement element, out TokenCounts tokens)
    {
        tokens = null!;
        if (!TryGetTokenLong(element, "inputTokens", out var input)) return false;
        if (!TryGetTokenLong(element, "outputTokens", out var output)) return false;
        if (!TryGetTokenLong(element, "cacheCreationTokens", out var cacheCreation)) return false;
        if (!TryGetTokenLong(element, "cacheReadTokens", out var cacheRead)) return false;

        var total = input + output + cacheCreation + cacheRead;
        tokens = new TokenCounts(input, output, cacheCreation, cacheRead, total);
        return true;
    }

    private static bool TryGetTokenLong(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number)
            return false;
        if (!property.TryGetInt64(out value)) return false;
        return value >= 0 && value <= MaxTokenValue;
    }

    private static bool TryParseIsoOffset(string? value, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var span = value.AsSpan().Trim();
        if (span.IsEmpty) return false;

        bool hasOffset = span[^1] == 'Z' || span[^1] == 'z';
        if (!hasOffset)
        {
            var lastPlus = span.LastIndexOf('+');
            var lastMinus = span.LastIndexOf('-');
            var signPos = Math.Max(lastPlus, lastMinus);
            if (signPos > 0 && span.Slice(signPos).Contains(':'))
            {
                hasOffset = true;
            }
        }

        if (!hasOffset) return false;

        return DateTimeOffset.TryParse(span.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    private static IReadOnlyList<string> ReadUnpricedModels(JsonElement root)
    {
        if (!root.TryGetProperty("totals", out var totals) || totals.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        if (!totals.TryGetProperty("unpricedModels", out var unpriced) || unpriced.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in unpriced.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) continue;
            var value = element.GetString();
            if (!string.IsNullOrEmpty(value)) set.Add(value);
        }

        var list = set.ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
