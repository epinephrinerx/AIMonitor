using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AIMonitor.Domain;
using Application = System.Windows.Application;

namespace AIMonitor.Presentation.Wpf.Tray;

/// <summary>
/// Manages the system tray icon using System.Windows.Forms.NotifyIcon directly.
/// Satisfies PAR-022, PAR-025, and ADR-0001:
/// - Generates dynamic tray icon based on short-window usage with severity colors
/// - Rotates active providers every 2 seconds
/// - Preserves last good reading on refresh failure
/// - Provides live context menu and tooltip
/// </summary>
public sealed class TrayIconHost : ITrayHost
{
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _rotationTimer;
    private readonly List<TrayReading> _readings = [];
    private int _currentReadingIndex;
    private IntPtr _currentHicon = IntPtr.Zero;
    private bool _disposed;

    public event Action? OpenDashboardRequested;
    public event Action? OpenWidgetRequested;
    public event Action? OpenLogRequested;
    public event Action? RefreshRequested;
    public event Action? OpenSettingsRequested;
    public event Action? OpenAboutRequested;
    public event Action? ExitRequested;

    public TrayIconHost()
    {
        _notifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "AIMonitor 2.0"
        };

        _notifyIcon.DoubleClick += (s, e) => OpenDashboardRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (s, e) => OpenDashboardRequested?.Invoke());
        menu.Items.Add("Open Widget", null, (s, e) => OpenWidgetRequested?.Invoke());
        menu.Items.Add("Usage Log...", null, (s, e) => OpenLogRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh Now", null, (s, e) => RefreshRequested?.Invoke());
        menu.Items.Add("Settings...", null, (s, e) => OpenSettingsRequested?.Invoke());
        menu.Items.Add("About...", null, (s, e) => OpenAboutRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (s, e) => ExitRequested?.Invoke());
        _notifyIcon.ContextMenuStrip = menu;

        // 2-second rotation timer (PAR-022)
        _rotationTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _rotationTimer.Tick += (s, e) => RotateReading();
        _rotationTimer.Start();

        UpdateIcon(0, Severity.Normal, "AI");
    }

    public void UpdateReadings(IEnumerable<TrayReading> readings)
    {
        if (_disposed) return;

        var list = readings.Where(r => r.HasData).ToList();
        if (list.Count > 0)
        {
            _readings.Clear();
            _readings.AddRange(list);
            _currentReadingIndex %= _readings.Count;
            ApplyCurrentReading();
        }
    }

    private void RotateReading()
    {
        if (_readings.Count <= 1) return;
        _currentReadingIndex = (_currentReadingIndex + 1) % _readings.Count;
        ApplyCurrentReading();
    }

    private void ApplyCurrentReading()
    {
        if (_readings.Count == 0)
        {
            UpdateIcon(0, Severity.Normal, "AI");
            SetTooltip("AIMonitor 2.0");
            return;
        }

        var reading = _readings[_currentReadingIndex];
        UpdateIcon(reading.Percentage, reading.Severity, reading.ProviderCode);

        var tooltip = $"AIMonitor: {reading.ProviderName} {reading.Percentage:F0}% ({reading.Detail})";
        SetTooltip(tooltip);
    }

    private void SetTooltip(string text)
    {
        // NotifyIcon.Text max length is 63 chars (or 127 on Win10/11)
        var maxLen = 63;
        _notifyIcon.Text = text.Length > maxLen ? text[..maxLen] : text;
    }

    private void UpdateIcon(double percentage, Severity severity, string code)
    {
        try
        {
            const int size = 16;
            using var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                // Outline track
                using var trackPen = new Pen(Color.FromArgb(80, 120, 120, 120), 2f);
                g.DrawEllipse(trackPen, 1, 1, 14, 14);

                // Progress arc
                var color = severity switch
                {
                    Severity.Critical => Color.FromArgb(239, 68, 68),  // Red (90%+)
                    Severity.VeryHigh or Severity.High => Color.FromArgb(245, 158, 11),  // Amber (75%+)
                    _ => Color.FromArgb(16, 185, 129)                 // Green
                };

                var sweep = (float)(Math.Clamp(percentage, 0.0, 100.0) / 100.0 * 360.0);
                if (sweep > 1f)
                {
                    using var progressPen = new Pen(color, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawArc(progressPen, 1, 1, 14, 14, -90, sweep);
                }

                // Inner glyph / letter
                using var font = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var textBrush = new SolidBrush(Color.White);
                var textSize = g.MeasureString(code, font);
                g.DrawString(code, font, textBrush, (size - textSize.Width) / 2f, (size - textSize.Height) / 2f + 0.5f);
            }

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

        if (_currentHicon != IntPtr.Zero)
        {
            DestroyIcon(_currentHicon);
            _currentHicon = IntPtr.Zero;
        }
    }
}

public sealed record TrayReading(
    string ProviderId,
    string ProviderName,
    string ProviderCode,
    double Percentage,
    Severity Severity,
    string Detail,
    bool HasData = true);
