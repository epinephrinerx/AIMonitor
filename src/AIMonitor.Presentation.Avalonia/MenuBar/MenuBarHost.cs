using AIMonitor.Domain;
using AIMonitor.Presentation.Avalonia.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AIMonitor.Presentation.Avalonia.MenuBar;

/// <summary>
/// The menu bar extra: the macOS counterpart of the Windows tray icon.
///
/// macOS opens the menu on a plain click rather than a right-click, and has no double-click
/// convention for a status item, so "Open AI Usage Monitor" is the first item in the menu instead of
/// a hidden gesture.
/// </summary>
internal sealed class MenuBarHost : IDisposable
{
    /// <summary>The Windows tray cycles services at this rate; the same pace reads well here.</summary>
    private static readonly TimeSpan RotateEvery = TimeSpan.FromSeconds(2);

    private readonly TrayIcon _icon = new();
    private readonly DispatcherTimer _rotation;
    private readonly Dictionary<string, IReadOnlyList<MeterViewModel>> _lastGood = new(StringComparer.Ordinal);
    private IReadOnlyList<MenuBarReading> _readings = [];
    private int _position;
    private bool _disposed;

    public MenuBarHost()
    {
        _icon.ToolTipText = "AI Usage Monitor";
        _icon.Menu = new NativeMenu();
        _rotation = new DispatcherTimer { Interval = RotateEvery };
        _rotation.Tick += (_, _) => Advance();
        Rebuild();
    }

    public event Action? OpenDashboardRequested;

    public event Action? OpenWidgetRequested;

    public event Action? RefreshRequested;

    public event Action? QuitRequested;

    public bool IsVisible
    {
        get => _icon.IsVisible;
        set => _icon.IsVisible = value;
    }

    /// <summary>The service the icon is currently showing, for tests and for the tooltip.</summary>
    public MenuBarReading? Current =>
        _readings.Count == 0 ? null : _readings[Math.Clamp(_position, 0, _readings.Count - 1)];

    public void UpdateReadings(IEnumerable<ProviderViewModel> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _readings = MenuBarReadings.Build(providers, _lastGood);

        var withData = _readings.Count(reading => reading.HasData);
        if (withData > 1)
        {
            _rotation.Start();
        }
        else
        {
            // Nothing to cycle between: a status item that redraws every two seconds for no reason
            // is just movement in the corner of the user's eye.
            _rotation.Stop();
            _position = 0;
        }

        Rebuild();
    }

    private void Advance()
    {
        if (_readings.Count == 0) return;
        _position = (_position + 1) % _readings.Count;
        Draw();
    }

    private void Rebuild()
    {
        Draw();

        var menu = new NativeMenu();
        menu.Add(Command("Open AI Usage Monitor", () => OpenDashboardRequested?.Invoke()));
        menu.Add(Command("Show widget", () => OpenWidgetRequested?.Invoke()));
        menu.Add(new NativeMenuItemSeparator());

        var now = DateTimeOffset.Now;
        var any = false;
        foreach (var reading in _readings.Where(reading => reading.Windows.Count > 0))
        {
            any = true;
            menu.Add(Label(MenuBarText.Header(reading)));
            foreach (var window in reading.Windows)
            {
                menu.Add(Label("      " + MenuBarText.QuotaLine(window, now, TimeZoneInfo.Local)));
            }
        }

        if (!any)
        {
            menu.Add(Label("No quota data yet"));
        }

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Command("Refresh now", () => RefreshRequested?.Invoke()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Command("Quit AI Usage Monitor", () => QuitRequested?.Invoke()));

        _icon.Menu = menu;
    }

    private void Draw()
    {
        var reading = Current;
        _icon.Icon = MenuBarIconRenderer.Render(
            reading?.Percent,
            reading?.Severity ?? Severity.Normal,
            Resource("SystemControlBackgroundListLowBrush", Brushes.Gainsboro),
            SeverityBrush(reading?.Severity ?? Severity.Normal),
            Resource("SystemControlForegroundBaseHighBrush", Brushes.Black));

        _icon.ToolTipText = reading is null
            ? "AI Usage Monitor"
            : MenuBarText.Tooltip(reading, _position, _readings.Count, DateTimeOffset.Now, TimeZoneInfo.Local);
    }

    private static IBrush SeverityBrush(Severity severity) => Resource(
        severity switch
        {
            Severity.High => "SeverityHighBrush",
            Severity.VeryHigh => "SeverityVeryHighBrush",
            Severity.Critical => "SeverityCriticalBrush",
            _ => "SeverityNormalBrush",
        },
        Brushes.Teal);

    /// <summary>
    /// Resolves a brush the application defines. The fallback keeps the icon drawable rather than
    /// letting a renamed resource take the whole menu bar extra down.
    /// </summary>
    private static IBrush Resource(string key, IBrush fallback)
    {
        // Fully qualified: AIMonitor.Application is a namespace of this solution, so a plain
        // "Application" here binds to it rather than to the toolkit's class.
        var application = global::Avalonia.Application.Current;
        if (application is not null
            && application.TryGetResource(key, application.ActualThemeVariant, out var value)
            && value is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }

    private static NativeMenuItem Command(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A row that reports a reading. Disabled because there is nothing to click.</summary>
    private static NativeMenuItem Label(string header) => new(header) { IsEnabled = false };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rotation.Stop();
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
