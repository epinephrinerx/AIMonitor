using AIMonitor.Application.Settings;
using AIMonitor.Presentation.Avalonia.ViewModels;

namespace AIMonitor.Presentation.Avalonia.Tests.ViewModels;

/// <summary>Monthly budgets on the settings page (R-015): one row per agent, saved with everything else.</summary>
public sealed class SettingsBudgetsTests
{
    private static async Task<(SettingsViewModel Model, SettingsSession Session)> BuildAsync(
        IReadOnlyDictionary<string, decimal>? budgets = null, IReadOnlyList<string>? knownAgents = null)
    {
        var settings = new AppSettings
        {
            AgentBudgets = budgets ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
        };
        var session = await SettingsSession.CreateAsync(new InMemorySettingsStore(settings));
        return (new SettingsViewModel(session, knownAgents), session);
    }

    [Fact]
    public async Task OpenCodeAlwaysHasARow_AndComesFirst()
    {
        var (model, _) = await BuildAsync(knownAgents: ["codex", "claude"]);

        Assert.Equal(["opencode", "claude", "codex"], model.BudgetRows.Select(row => row.Agent));
        Assert.Equal(["OpenCode", "Claude Code", "Codex"], model.BudgetRows.Select(row => row.DisplayName));
    }

    [Fact]
    public async Task ASavedBudget_IsShownWithTwoDecimals()
    {
        var (model, _) = await BuildAsync(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["opencode"] = 50m });

        Assert.Equal("50.00", model.BudgetRows[0].Text);
    }

    [Fact]
    public async Task SavingWritesTheBudgets_RoundedToCents_AndLeavesEmptyRowsOut()
    {
        var (model, session) = await BuildAsync(knownAgents: ["claude"]);
        model.BudgetRows[0].Text = "12.345";

        await model.SaveAsync();

        Assert.Equal(12.35m, session.Current.AgentBudgets["opencode"]);
        Assert.False(session.Current.AgentBudgets.ContainsKey("claude"));
    }

    [Fact]
    public async Task AnInvalidBudget_BlocksSave()
    {
        var (model, session) = await BuildAsync();
        model.BudgetRows[0].Text = "not money";

        Assert.True(model.HasBudgetError);
        Assert.False(model.SaveCommand.CanExecute(null));

        await model.SaveAsync();
        Assert.Empty(session.Current.AgentBudgets);
    }

    [Fact]
    public async Task Cancel_PutsTheBudgetsBack()
    {
        var (model, session) = await BuildAsync(new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase) { ["opencode"] = 50m });
        model.BudgetRows[0].Text = "999";

        model.Cancel();

        Assert.Equal("50.00", model.BudgetRows[0].Text);
        Assert.Equal(50m, session.Current.AgentBudgets["opencode"]);
    }
}
