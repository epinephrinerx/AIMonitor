using Avalonia.Input;

namespace AIMonitor.Presentation.Avalonia.Tests;

/// <summary>The menu's shortcuts are parsed while the app starts, so a typo would stop it starting.</summary>
public sealed class AppShortcutsTests
{
    [Fact]
    public void EveryShortcut_Parses_AndUsesCommand()
    {
        foreach (var text in AppShortcuts.All)
        {
            var gesture = KeyGesture.Parse(text);
            Assert.True(gesture.KeyModifiers.HasFlag(KeyModifiers.Meta), text);
        }
    }

    [Fact]
    public void NoTwoMenuItems_ShareAShortcut()
    {
        Assert.Equal(AppShortcuts.All.Count, AppShortcuts.All.Select(text => KeyGesture.Parse(text).ToString()).Distinct().Count());
    }
}
