using AIMonitor.Domain;

namespace AIMonitor.Domain.Tests;

/// <summary>
/// Tests for <see cref="ModelUsage"/> and <see cref="AgentUsage"/> per SPEC R-014 §P1.1.
/// Exception type for blank-model validation is not named in the spec, so these assert the
/// <c>ArgumentException</c> family only (see handoff assumptions).
/// </summary>
public class AgentUsageModelUsageTests
{
    private static TokenCounts Tokens => new(100, 200, 300, 350, 1000);

    [Fact]
    public void ModelUsage_Ctor_PreservesModelTokensAndCost()
    {
        // SPEC P1.1: "Model non-blank, kept exactly as ccusage printed it." Mixed case must survive.
        var tokens = new TokenCounts(60, 80, 20, 40, 200); // hand calc: 60+80+20+40 = 200, Other = 0.
        var model = new ModelUsage("Claude-Opus-4-8", tokens, 0.0425m);

        Assert.Equal("Claude-Opus-4-8", model.Model);
        Assert.Equal(60, model.Tokens.Input);
        Assert.Equal(200, model.Tokens.Total);
        Assert.Equal(0.0425m, model.CostUsd);
    }

    [Fact]
    public void ModelUsage_NullCostMeansUnknownPrice_AndIsKeptAsNullNotZero()
    {
        // SPEC P1.1: "CostUsd == null means unknown price (ccusage missingPricing: true). Never 0 for unknown."
        var unknown = new ModelUsage("gemini-3", Tokens, null);
        var pricedAtZero = new ModelUsage("free-model", Tokens, 0.0m);

        Assert.Null(unknown.CostUsd);
        Assert.NotNull(pricedAtZero.CostUsd);
        Assert.Equal(0.0m, pricedAtZero.CostUsd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ModelUsage_BlankModel_Throws(string model)
    {
        // SPEC P1.1: "Model non-blank".
        Assert.ThrowsAny<ArgumentException>(() => new ModelUsage(model, Tokens, 0.0m));
    }

    [Fact]
    public void ModelUsage_NullModel_Throws()
    {
        // SPEC P1.1: "Model non-blank" (null is not a non-blank string).
        Assert.ThrowsAny<ArgumentException>(() => new ModelUsage(null!, Tokens, 0.0m));
    }

    [Theory]
    [InlineData("  Claude  ", "claude")]
    [InlineData("ANTIGRAVITY", "antigravity")]
    [InlineData("CoDeX", "codex")]
    public void AgentUsage_IdIsTrimmedAndLowerCaseInvariant(string raw, string expected)
    {
        // SPEC P1.1: "Agent = ccusage agent id, trimmed, lower-case invariant".
        var agent = new AgentUsage(raw, Tokens, Array.Empty<ModelUsage>());

        Assert.Equal(expected, agent.Agent);
    }

    [Fact]
    public void AgentUsage_UnknownAgentId_IsKept()
    {
        // SPEC P1.1: "Unknown ids are kept, never dropped."
        var agent = new AgentUsage("zcode", Tokens, Array.Empty<ModelUsage>());

        Assert.Equal("zcode", agent.Agent);
    }

    [Fact]
    public void AgentUsage_PricedCostUsd_SpecOracle_ExcludesUnknownModel()
    {
        // SPEC P1.4 oracle: "models 1.25 + unknown → 1.25, HasUnpricedModels".
        // Hand calc: 1.25m (priced) + null (unknown) → PricedCostUsd = 1.25m.
        var models = new[]
        {
            new ModelUsage("claude-opus-4-8", Tokens, 1.25m),
            new ModelUsage("gemini-3", Tokens, null),
        };
        var agent = new AgentUsage("claude", Tokens, models);

        Assert.Equal(1.25m, agent.PricedCostUsd);
        Assert.True(agent.HasUnpricedModels);
    }

    [Fact]
    public void AgentUsage_PricedCostUsd_SumsEveryNonNullModelCost()
    {
        // SPEC P1.1: "PricedCostUsd => sum of non-null model costs".
        // Hand calc: 0.10m + 0.20m + 0.05m = 0.35m; the null model contributes nothing.
        var models = new[]
        {
            new ModelUsage("a", Tokens, 0.10m),
            new ModelUsage("b", Tokens, null),
            new ModelUsage("c", Tokens, 0.20m),
            new ModelUsage("d", Tokens, 0.05m),
        };
        var agent = new AgentUsage("claude", Tokens, models);

        Assert.Equal(0.35m, agent.PricedCostUsd);
    }

    [Fact]
    public void AgentUsage_PricedCostUsd_NoModels_IsZero()
    {
        // SPEC P1.1: sum over an empty set of non-null costs = 0.
        var agent = new AgentUsage("claude", Tokens, Array.Empty<ModelUsage>());

        Assert.Equal(0.0m, agent.PricedCostUsd);
        Assert.False(agent.HasUnpricedModels);
    }

    [Fact]
    public void AgentUsage_HasUnpricedModels_FalseWhenEveryModelIsPriced()
    {
        // SPEC P1.1: "HasUnpricedModels => any model CostUsd == null".
        var models = new[]
        {
            new ModelUsage("a", Tokens, 0.0m),
            new ModelUsage("b", Tokens, 1.0m),
        };
        var agent = new AgentUsage("claude", Tokens, models);

        Assert.False(agent.HasUnpricedModels);
    }

    [Fact]
    public void AgentUsage_Ctor_PreservesTokensAndModels()
    {
        var model = new ModelUsage("claude-opus-4-8", Tokens, 1.0m);
        var agent = new AgentUsage("claude", new TokenCounts(1, 2, 3, 4, 10), new[] { model });

        Assert.Equal(1, agent.Tokens.Input);
        Assert.Equal(10, agent.Tokens.Total);
        // SPEC P1.1 keeps the model list; identity of the caller's array is an implementation detail,
        // so assert content instead.
        Assert.Equal(new[] { model }, agent.Models);
    }
}
