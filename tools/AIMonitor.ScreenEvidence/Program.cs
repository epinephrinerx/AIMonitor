using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIMonitor.Application.AgentUsage;
using AIMonitor.Application.Providers;
using AIMonitor.Application.Reports;
using AIMonitor.Application.Settings;
using AIMonitor.Application.Time;
using AIMonitor.Application.Version;
using AIMonitor.Application.Windows;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf;
using AIMonitor.Presentation.Wpf.Theme;
using AIMonitor.Presentation.Wpf.Tray;
using AIMonitor.Presentation.Wpf.ViewModels;

namespace AIMonitor.ScreenEvidence;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: ScreenEvidence <fixture.json> <output-directory>");
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(args[0]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        _ = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var snapshots = fixture.Providers.Select(p => Snapshot(p, fixture)).ToDictionary(p => p.ProviderId);
        var manifest = new List<string>();
        // R-014–R-017: the OpenCode, Overview and All agents tabs are rendered from a second
        // view model fed by an in-memory agent usage source — no ccusage, no file, no network.
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var agentUsage = new CachedAgentUsageSource(
            new MemoryAgentUsageSource(SyntheticAgentUsage(now, today)), new FixedClock(now));
        foreach (var theme in new[] { "light", "dark" })
        {
            ThemeManager.Instance.ApplyTheme(theme);
            var settings = new AppSettings { Theme = theme, ShowConnectionsAtStartup = false, ShowTrayIcon = false, MinimizeToTray = false, RefreshIntervalSeconds = 0, ChartRangeDays = 7 };
            using var session = new SettingsSession(new MemorySettings(settings), settings);
            var coordinator = new LatestRefreshCoordinator(new RefreshProvidersUseCase(snapshots.Select(p => new ProviderClientRegistration(p.Key, new SampleProvider(p.Value))).ToArray()));
            var vm = new MainWindowViewModel(coordinator, session);
            vm.RefreshAsync().GetAwaiter().GetResult();
            vm.LastRefreshStatus = "Synthetic fixture · Manual refresh";
            var main = new MainWindow(vm, session) { Width = 1120, Height = 820 };
            foreach (var tab in vm.ProviderTabs)
            {
                vm.SelectedTab = tab;
                Capture(main, $"dashboard-{tab.ProviderId}-{theme}");
            }

            var detailTab = vm.ProviderTabs.Single(t => t.ProviderId == "openai");
            detailTab.UpdateFromSnapshot(new ProviderSnapshot("openai", true,
                stats: [new Stat("A long workspace bucket · Plan", "Business", "Reported by Codex"),
                    new Stat("Credits", "150", "Workspace credits"), new Stat("Rate limit resets", "2", "Next known expiry: 2026-10-20 18:00:00Z"),
                    new Stat("Current streak", "8 days"), new Stat("Longest streak", "14 days"), new Stat("Longest turn", "9 min (540s)")]));
            var detailsWindow = new Window
            {
                Content = new SupplementaryDetailsView { DataContext = detailTab },
                Width = 320, Height = 1100, Background = main.Background
            };
            Capture(detailsWindow, $"supplementary-narrow-{theme}");
            detailTab.Supplementary!.IsResetDetailsExpanded = true;
            Capture(detailsWindow, $"supplementary-expanded-{theme}");
            detailsWindow.Close();

            vm.IsConnectionsPageVisible = true;
            Capture(main, $"connections-{theme}");
            vm.IsConnectionsPageVisible = false;
            vm.SelectedTab = vm.ClaudeTab;
            main.Width = 300;
            main.Height = 220;
            Capture(main, $"dashboard-minimum-{theme}");
            main.RequestExplicitExit();

            // 2.0-only ccusage tabs (R-014–R-017): a second view model receives the synthetic
            // agent usage report; the first window keeps its current no-ccusage content.
            var usageSettings = settings with
            {
                AgentBudgets = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
                {
                    ["opencode"] = 50m, ["claude"] = 200m
                }
            };
            using var usageSession = new SettingsSession(new MemorySettings(usageSettings), usageSettings);
            var usageVm = new MainWindowViewModel(coordinator, usageSession, agentUsage: agentUsage);
            usageVm.RefreshAsync().GetAwaiter().GetResult();
            // The update arrives through the UI context; pump background-priority frames
            // instead of blocking on the agent-usage task.
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var missing =
                    usageVm.OpenCodeTab.Meters.Count == 0 ? "OpenCode" :
                    usageVm.OverviewTab.Meters.Count != 4 ? "Overview" :
                    usageVm.AllAgentsTab.Sessions.Count == 0 ? "All agents" : null;
                if (missing is null) break;
                if (DateTime.UtcNow >= deadline)
                {
                    throw new TimeoutException($"Synthetic agent usage did not reach the {missing} tab within 30 seconds.");
                }

                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            }

            usageVm.LastRefreshStatus = "Synthetic fixture · Manual refresh";
            var usageMain = new MainWindow(usageVm, usageSession) { Width = 1120, Height = 820 };
            foreach (var tabId in new[] { "opencode", "overview", "all-agents" })
            {
                // Fail closed when the render crosses local midnight.
                if (DateOnly.FromDateTime(DateTime.Now) != today)
                {
                    throw new InvalidOperationException("render crossed local midnight; rerun");
                }

                usageVm.SelectedTabItem = usageVm.Tabs.Single(tab =>
                    tab is ProviderTabViewModel provider && provider.ProviderId == tabId
                    || tab is AllAgentsTabViewModel agents && agents.TabId == tabId);
                Capture(usageMain, $"dashboard-{tabId}-{theme}");
            }

            usageMain.RequestExplicitExit();

            foreach (var size in new[] { (230d, 175d), (300d, 300d), (150d, 140d) })
            {
                var widgetVm = new WidgetViewModel(vm.ProviderTabs);
                widgetVm.ApplySettings(settings with { WidgetOpacity = 1, WidgetAlwaysOnTop = false, WidgetRotationEnabled = false });
                var widget = new WidgetWindow { DataContext = widgetVm, Width = size.Item1, Height = size.Item2 };
                Capture(widget, $"widget-{size.Item1}x{size.Item2}-{theme}");
                widget.Close();
            }

            var settingsVm = new SettingsViewModel(session, new SampleStartup(), _ => { });
            var settingsWindow = new SettingsDialog(settingsVm);
            Capture(settingsWindow, $"settings-{theme}");
            settingsWindow.Close();

            var connectionStore = new ProviderConnectionStore(new MemorySecrets(), session);
            foreach (var provider in fixture.Providers)
            {
                var connectVm = new ConnectDialogViewModel(ProviderMeta.TryGet(provider.Id)!, snapshots[provider.Id].Detection, new ProviderConnection(null), connectionStore);
                var connect = new ConnectDialog(connectVm);
                Capture(connect, $"connect-{provider.Id}-{theme}");
                connect.Close();
            }

            foreach (var page in Enum.GetValues<AboutPage>())
            {
                var about = new AboutDialog(new AboutViewModel(new SampleVersion()), page);
                Capture(about, $"about-{page.ToString().ToLowerInvariant()}-{theme}");
                about.Close();
            }

            var report = UsageReportGenerator.Build(fixture.Providers.Select(p => (p.Id, p.Name)), snapshots, 7, "Total tokens");
            var log = new UsageLogDialog(new UsageLogViewModel(report));
            Capture(log, $"usage-log-{theme}");
            log.Close();
            var reportPath = Path.Combine(output, $"report-{theme}");
            File.WriteAllText(reportPath + ".csv", UsageReportGenerator.ToCsv(report));
            File.WriteAllText(reportPath + ".md", UsageReportGenerator.ToMarkdown(report));
            File.WriteAllText(reportPath + ".html", UsageReportGenerator.ToHtml(report, dark: false));

            using (var tray = TrayIconRenderer.Render(42, Severity.Normal, ThemePalette.For(theme == "dark"), 64))
            {
                tray.Save(Path.Combine(output, $"tray-{theme}.png"));
            }

            coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Rendered {manifest.Count} WPF client-area screens with synthetic data. No provider network, credentials or startup registration used.");
        ThemeManager.Instance.Dispose();

        void Capture(Window window, string name)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            if (!window.IsVisible) window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            var width = (int)Math.Ceiling(content.ActualWidth);
            var height = (int)Math.Ceiling(content.ActualHeight);
            if (width <= 0 || height <= 0) throw new InvalidOperationException($"Empty screen: {name}");
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
                context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
            }

            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(stream);
            manifest.Add(name);
        }
    }

    private static ProviderSnapshot Snapshot(ProviderFixture p, Fixture fixture) => new(
        p.Id, configured: true,
        meters: p.Meters.Select(m => new Meter(m.Kind, m.Key, m.Title, m.Subtitle, m.Percent)).ToArray(),
        stats: p.Id == "agy" ? [] : fixture.Stats.Select(s => new Stat(s.Label, s.Value)).Concat(p.Id == "openai"
            ? [new Stat("Plan", "Plus", "ChatGPT plan"), new Stat("Credits", "150", "Workspace credits"),
                new Stat("Rate limit resets", "2", "Next known expiry: 2026-10-20 18:00:00Z"), new Stat("Current streak", "8 days")]
            : Array.Empty<Stat>()).ToArray(),
        history: p.Id == "agy" ? null : new UsageHistory(
            fixture.Buckets.Select(b => new UsageHistoryBucket(DateOnly.Parse(b.Day), b.Values)).ToArray(),
            ["Model A", "Model B"],
            [new UsageHistoryBreakdown("Model A", 42000), new UsageHistoryBreakdown("Model B", 21000)],
            [new UsageHistoryBreakdown("Synthetic project", 63000)], days: 7),
        account: "Synthetic account", valueNote: p.Id == "agy" ? "AGY shared model-pool quota · Synthetic fixture" : "Equivalent API value is an estimate.",
        detection: new DetectionInfo(p.Id, DetectionState.Connected, "fixture", "Synthetic login", "Synthetic account"));

    private sealed class SampleProvider(ProviderSnapshot snapshot) : IProviderQuotaClient
    {
        public Task<ProviderSnapshot> GetSnapshotAsync(ProviderSnapshotRequest request, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class MemorySettings(AppSettings initial) : ISettingsStore
    {
        private AppSettings _settings = initial;
        public bool Exists => true;
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_settings);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { _settings = settings; return Task.CompletedTask; }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetAsync(string name, string value, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Screen evidence must not store keys.");
        public Task RemoveAsync(string name, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Screen evidence must not delete keys.");
    }

    private sealed class SampleStartup : IStartupRegistrar
    {
        public bool IsRegistered() => false;
        public void Register(string executablePath, string arguments = "") => throw new InvalidOperationException("Screen evidence must not register startup.");
        public void Unregister() => throw new InvalidOperationException("Screen evidence must not unregister startup.");
    }

    private sealed class SampleVersion : IVersionChecker
    {
        public Task<ReleaseInfo> CheckLatestAsync(string installedVersion, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Screen evidence must not query GitHub.");
    }

    /// <summary>
    /// Decision V6: one in-memory ccusage-style report. Daily rows cover the 7 days ending today
    /// with day factors 1..7; the base unit per model row is 1,000 input / 500 output /
    /// 8,500 cache-read tokens (10,000 total) and USD 0.50, scaled by weight·factor.
    /// "Model X" carries no price. Monthly rows sum the calendar month's daily rows and the
    /// sessions hold today's row per agent. Relative to the local date at render time.
    /// </summary>
    private static AgentUsageReport SyntheticAgentUsage(DateTimeOffset now, DateOnly today)
    {
        const long baseInput = 1000, baseOutput = 500, baseCacheRead = 8500, baseTotal = 10000;
        const decimal baseCost = 0.50m;
        (string Agent, string Model, int Weight)[] plan =
        [
            ("claude", "Model A", 4), ("claude", "Model B", 1),
            ("codex", "Model A", 2),
            ("opencode", "Model A", 1), ("opencode", "Model B", 2), ("opencode", "Model X", 1),
            ("copilot", "Model B", 1),
        ];

        static TokenCounts Scale(long units) => new(
            baseInput * units, baseOutput * units, 0, baseCacheRead * units, baseTotal * units);
        static ModelUsage Row(string model, long units) => new(
            model, Scale(units), model == "Model X" ? null : baseCost * units);
        static TokenCounts Sum(IReadOnlyList<ModelUsage> models)
        {
            long input = 0, output = 0, cacheCreation = 0, cacheRead = 0, total = 0;
            foreach (var model in models)
            {
                input += model.Tokens.Input;
                output += model.Tokens.Output;
                cacheCreation += model.Tokens.CacheCreation;
                cacheRead += model.Tokens.CacheRead;
                total += model.Tokens.Total;
            }

            return new TokenCounts(input, output, cacheCreation, cacheRead, total);
        }

        var daily = new List<DailyAgentUsage>();
        var todayRows = new Dictionary<string, AgentUsage>();
        var monthlyUnits = new Dictionary<(int Year, int Month), Dictionary<string, Dictionary<string, long>>>();
        for (var i = 0; i < 7; i++)
        {
            var day = today.AddDays(-(6 - i));
            var factor = i + 1;
            var agents = new List<AgentUsage>();
            foreach (var group in plan.GroupBy(entry => entry.Agent))
            {
                var models = new List<ModelUsage>();
                foreach (var entry in group)
                {
                    var units = (long)entry.Weight * factor;
                    models.Add(Row(entry.Model, units));
                    var month = (day.Year, day.Month);
                    if (!monthlyUnits.TryGetValue(month, out var byAgent))
                    {
                        monthlyUnits[month] = byAgent = [];
                    }

                    if (!byAgent.TryGetValue(entry.Agent, out var byModel))
                    {
                        byAgent[entry.Agent] = byModel = [];
                    }

                    byModel[entry.Model] = byModel.GetValueOrDefault(entry.Model) + units;
                }

                var row = new AgentUsage(group.Key, Sum(models), models);
                agents.Add(row);
                if (day == today)
                {
                    todayRows[group.Key] = row;
                }
            }

            daily.Add(new DailyAgentUsage(day, agents));
        }

        var monthly = new List<MonthlyAgentUsage>();
        foreach (var (month, byAgent) in monthlyUnits.OrderBy(pair => pair.Key))
        {
            var agents = new List<AgentUsage>();
            foreach (var group in plan.GroupBy(entry => entry.Agent))
            {
                if (!byAgent.TryGetValue(group.Key, out var byModel))
                {
                    continue;
                }

                var models = new List<ModelUsage>();
                foreach (var entry in group)
                {
                    models.Add(Row(entry.Model, byModel[entry.Model]));
                }

                agents.Add(new AgentUsage(group.Key, Sum(models), models));
            }

            monthly.Add(new MonthlyAgentUsage(month.Item1, month.Item2, agents));
        }

        var sessions = plan.GroupBy(entry => entry.Agent)
            .Select(group => new SessionAgentUsage(
                $"synthetic-{group.Key}", now.AddHours(-1), todayRows[group.Key]))
            .ToList();

        return new AgentUsageReport(
            daily, monthly, sessions, ["Model X"], AgentUsagePricing.OnlineRequested, now, skippedRows: 0);
    }

    private sealed class MemoryAgentUsageSource(AgentUsageReport report) : IAgentUsageSource
    {
        public Task<AgentUsageReport> ReadAsync(DateOnly? since, CancellationToken ct) => Task.FromResult(report);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed record Fixture(ProviderFixture[] Providers, StatFixture[] Stats, BucketFixture[] Buckets);
    private sealed record ProviderFixture(string Id, string Name, MeterFixture[] Meters);
    private sealed record MeterFixture(string Kind, string Key, string Title, string Subtitle, double? Percent);
    private sealed record StatFixture(string Label, string Value);
    private sealed record BucketFixture(string Day, Dictionary<string, double> Values);
}
