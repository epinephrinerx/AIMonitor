using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace AIMonitor.Presentation.Wpf.Theme;

/// <summary>
/// Manages application themes (System, Light, Dark) and severity brushes according to PAR-028.
/// Detects Windows system theme dynamically via registry and SystemEvents.
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private string _currentThemePreference = "system";
    private bool _isDark;
    private bool _disposed;

    public static ThemeManager Instance { get; } = new();

    public ThemeManager()
    {
        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch
        {
            // Best effort in case system events cannot be hooked
        }
    }

    public event Action? ThemeChanged;

    public bool IsDark => _isDark;

    public string CurrentThemePreference => _currentThemePreference;

    public void ApplyTheme(string themePreference)
    {
        _currentThemePreference = themePreference is "light" or "dark" ? themePreference : "system";
        _isDark = _currentThemePreference switch
        {
            "light" => false,
            "dark" => true,
            _ => DetectSystemIsDark()
        };

        UpdateApplicationResources();
        ThemeChanged?.Invoke();
    }

    public static bool DetectSystemIsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue(AppsUseLightThemeValue) is int lightThemeValue)
            {
                return lightThemeValue == 0;
            }
        }
        catch
        {
            // Default to dark theme if registry access fails
        }

        return true;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_currentThemePreference == "system")
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => ApplyTheme("system"));
        }
    }

    private void UpdateApplicationResources()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var dict = app.Resources;
        var p = ThemePalette.For(_isDark);

        // Background & Surface
        dict["AppBackgroundBrush"] = Solid(p.Plane);
        dict["CardBackgroundBrush"] = Solid(p.Surface);
        dict["CardHoverBrush"] = Solid(p.SurfaceHover);
        dict["BorderBrush"] = Solid(p.Border);
        dict["TrackBrush"] = Solid(p.Track);
        dict["GridBrush"] = Solid(p.Grid);
        dict["BaselineBrush"] = Solid(p.Baseline);

        // Text & Foreground
        dict["TextPrimaryBrush"] = Solid(p.Ink);
        dict["TextSecondaryBrush"] = Solid(p.InkSecondary);
        dict["TextMutedBrush"] = Solid(p.InkMuted);

        // Brand / Accent
        dict["AccentBrush"] = Solid(p.Accent);
        dict["AccentHoverBrush"] = Solid(p.AccentHover);

        // Severities (PAR-016, PAR-028): fixed traffic light, never themed
        dict["SeverityNormalBrush"] = Solid(ThemePalette.StatusGood);
        dict["SeverityWarningBrush"] = Solid(ThemePalette.StatusWarning);
        dict["SeverityVeryHighBrush"] = Solid(ThemePalette.StatusSerious);
        dict["SeverityCriticalBrush"] = Solid(ThemePalette.StatusCritical);
        dict["SeverityInactiveBrush"] = Solid(p.InkMuted);

        // Categorical series (model / project colours)
        for (var i = 0; i < p.Categorical.Count; i++)
        {
            dict[$"Series{i + 1}Brush"] = Solid(p.Categorical[i]);
        }
    }

    /// <summary>Resolves the current traffic-light brush for a severity from application resources.</summary>
    public static Brush SeverityBrush(AIMonitor.Domain.Severity severity)
    {
        var key = severity switch
        {
            AIMonitor.Domain.Severity.High => "SeverityWarningBrush",
            AIMonitor.Domain.Severity.VeryHigh => "SeverityVeryHighBrush",
            AIMonitor.Domain.Severity.Critical => "SeverityCriticalBrush",
            _ => "SeverityNormalBrush",
        };
        return System.Windows.Application.Current?.Resources[key] as Brush
            ?? Solid(ThemePalette.StatusFor(severity));
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch
        {
        }
    }
}
