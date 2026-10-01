using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
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

        // Background & Surface
        dict["AppBackgroundBrush"] = new SolidColorBrush(_isDark ? Color.FromRgb(20, 22, 26) : Color.FromRgb(245, 247, 250));
        dict["CardBackgroundBrush"] = new SolidColorBrush(_isDark ? Color.FromRgb(30, 34, 42) : Color.FromRgb(255, 255, 255));
        dict["CardHoverBrush"] = new SolidColorBrush(_isDark ? Color.FromRgb(38, 43, 54) : Color.FromRgb(240, 243, 246));
        dict["BorderBrush"] = new SolidColorBrush(_isDark ? Color.FromRgb(48, 54, 66) : Color.FromRgb(226, 232, 240));

        // Text & Foreground
        dict["TextPrimaryBrush"] = new SolidColorBrush(_isDark ? Color.FromRgb(243, 244, 246) : Color.FromRgb(17, 24, 39));
        dict["TextSecondaryBrush"] = new SolidColorBrush(_isDark ? Color.FromRgb(156, 163, 175) : Color.FromRgb(107, 114, 128));
        dict["TextMutedBrush"] = new SolidColorBrush(_isDark ? Color.FromRgb(107, 114, 128) : Color.FromRgb(156, 163, 175));

        // Brand / Accent
        dict["AccentBrush"] = new SolidColorBrush(Color.FromRgb(59, 130, 246)); // Blue
        dict["AccentHoverBrush"] = new SolidColorBrush(Color.FromRgb(37, 99, 235));

        // Severities (PAR-016, PAR-028)
        dict["SeverityNormalBrush"] = new SolidColorBrush(Color.FromRgb(16, 185, 129));   // Emerald Green
        dict["SeverityWarningBrush"] = new SolidColorBrush(Color.FromRgb(245, 158, 11));  // Amber (75%+)
        dict["SeverityCriticalBrush"] = new SolidColorBrush(Color.FromRgb(239, 68, 68));  // Red (90%+)
        dict["SeverityInactiveBrush"] = new SolidColorBrush(Color.FromRgb(156, 163, 175)); // Gray
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
