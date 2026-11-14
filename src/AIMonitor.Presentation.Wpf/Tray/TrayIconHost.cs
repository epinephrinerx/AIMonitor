using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AIMonitor.Domain;
using AIMonitor.Presentation.Wpf.Theme;

namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>
/// Manages the system tray icon using System.Windows.Forms.NotifyIcon directly. Follows 1.3.3 <c>tray.py</c>
/// (PAR-022, PAR-025, ADR-0001):
/// - the icon is a tile filled to the five-hour window's percentage, with the number across it
/// - one icon rotates every two seconds over services that have such a reading, and stays still for one
/// - the last good reading survives a failed refresh (marked stale)
/// - the menu lists every quota window per service with a live countdown, rebuilt each time it opens
/// - the tooltip names the service, the window, its severity and when it resets
/// </summary>
public sealed class TrayIconHost : ITrayHost, ITrayNotifier
{
    private const int RotateMilliseconds = 2000;

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _rotationTimer;
    private readonly Func<DateTimeOffset> _clock;
    private List<TrayReading> _all = [];
    private List<TrayReading> _rotation = [];
    private int _currentIndex;
    private IntPtr _currentHicon = IntPtr.Zero;
    private ContextMenuStrip? _contextMenu;
    private bool _disposed;

    public event Action? OpenDashboardRequested;
    public event Action? OpenWidgetRequested;
    public event Action? RefreshRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public TrayIconHost(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _notifyIcon = new NotifyIcon { Visible = true, Text = "AI Usage Monitor" };

        // Double-click restores the full window; right-click opens the menu (the platform convention).
        _notifyIcon.DoubleClick += (s, e) => OpenDashboardRequested?.Invoke();

        _contextMenu = new ContextMenuStrip();
        // Rebuilt every time it opens: the quota rows carry live countdowns.
        _contextMenu.Opening += (s, e) => RebuildMenu();
        RebuildMenu();
        _notifyIcon.ContextMenuStrip = _contextMenu;

        _rotationTimer = new System.Windows.Forms.Timer { Interval = RotateMilliseconds };
        _rotationTimer.Tick += (s, e) => Advance();

        Render();
    }

    public void UpdateReadings(IEnumerable<TrayReading> readings)
    {
        if (_disposed) return;

        _all = readings.ToList();
        var order = _all.Where(r => r.IconMeter is not null).ToList();
        if (!order.Select(r => r.ProviderId).SequenceEqual(_rotation.Select(r => r.ProviderId)))
        {
            _currentIndex = 0;
        }

        _rotation = order;

        // A single service has nothing to rotate through; leave it still.
        if (_rotation.Count > 1)
        {
            _rotationTimer.Start();
        }
        else
        {
            _rotationTimer.Stop();
        }

        Render();
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed || !_notifyIcon.Visible) return;
        _notifyIcon.ShowBalloonTip(4000, title, message, ToolTipIcon.Info);
    }

    private void Advance()
    {
        if (_rotation.Count <= 1) return;
        _currentIndex = (_currentIndex + 1) % _rotation.Count;
        Render();
    }

    private void Render()
    {
        var palette = ThemePalette.For(ThemeManager.Instance.IsDark);
        if (_rotation.Count == 0)
        {
            SetIcon(null, Severity.Normal, palette);
            SetTooltip("AI Usage Monitor — no quota data yet");
            return;
        }

        _currentIndex %= _rotation.Count;
        var reading = _rotation[_currentIndex];
        var meter = reading.IconMeter!;
        SetIcon(meter.Value, meter.Severity, palette);
        SetTooltip(TrayText.Tooltip(reading, _currentIndex, _rotation.Count, _clock(), TimeZoneInfo.Local));
    }

    /// <summary>Resume, Show Widget, every quota window per service, Refresh now, Setting, Exit.</summary>
    private void RebuildMenu()
    {
        if (_contextMenu is null) return;
        _contextMenu.Items.Clear();
        _contextMenu.Items.Add("Resume", null, (s, e) => OpenDashboardRequested?.Invoke());
        _contextMenu.Items.Add("Show Widget", null, (s, e) => OpenWidgetRequested?.Invoke());
        _contextMenu.Items.Add(new ToolStripSeparator());

        var now = _clock();
        var added = false;
        foreach (var reading in _all.Where(r => r.Windows.Count > 0))
        {
            _contextMenu.Items.Add(new ToolStripMenuItem(TrayText.Header(reading)) { Enabled = false });
            foreach (var window in reading.Windows)
            {
                _contextMenu.Items.Add(new ToolStripMenuItem("      " + TrayText.QuotaLine(window, now, TimeZoneInfo.Local)) { Enabled = false });
                added = true;
            }

            _contextMenu.Items.Add(new ToolStripSeparator());
        }

        if (!added)
        {
            _contextMenu.Items.Add(new ToolStripMenuItem("No quota data yet") { Enabled = false });
        }

        _contextMenu.Items.Add("Refresh now", null, (s, e) => RefreshRequested?.Invoke());
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Setting", null, (s, e) => OpenSettingsRequested?.Invoke());
        _contextMenu.Items.Add("Exit", null, (s, e) => ExitRequested?.Invoke());
    }

    private void SetTooltip(string text) =>
        _notifyIcon.Text = text.Length > TrayText.MaxTooltipLength ? TrayText.Fit([text]) : text;

    private void SetIcon(double? percent, Severity severity, ThemePalette palette)
    {
        try
        {
            var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using var bitmap = TrayIconRenderer.Render(percent, severity, palette, size);
            var oldHicon = _currentHicon;
            _currentHicon = bitmap.GetHicon();

            using var icon = Icon.FromHandle(_currentHicon);
            _notifyIcon.Icon = icon;

            if (oldHicon != IntPtr.Zero)
            {
                DestroyIcon(oldHicon);
            }
        }
        catch
        {
            // Best effort icon drawing
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _rotationTimer.Stop();
        _rotationTimer.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        if (_contextMenu is not null)
        {
            _contextMenu.Dispose();
            _contextMenu = null;
        }

        if (_currentHicon != IntPtr.Zero)
        {
            DestroyIcon(_currentHicon);
            _currentHicon = IntPtr.Zero;
        }
    }
}
