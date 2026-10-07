using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using AIMonitor.Application.Version;

namespace AIMonitor.Presentation.Wpf.ViewModels;

/// <summary>The About menu's pages; each opens as its own window, as in 1.3.3.</summary>
public enum AboutPage
{
    Version,
    Readme,
    License,
    Notices,
    Developer,
}

public sealed class AboutViewModel : ViewModelBase
{
    private const string UpdateNote = "Checking for updates is a read-only request to GitHub. Nothing is downloaded or installed.";
    private const string ReleasesUrl = "https://github.com/epinephrinerx/AIMonitor/releases";

    private readonly IVersionChecker _versionChecker;
    private string _updateStatus = UpdateNote;
    private bool _isChecking;
    private bool _isUpdateAvailable;
    private ReleaseInfo? _latestRelease;

    public AboutViewModel(IVersionChecker? versionChecker = null)
    {
        _versionChecker = versionChecker ?? new GitHubVersionChecker();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersion = ver is not null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "2.0.0";

        OpenProjectUrlCommand = new RelayCommand(() => OpenUrl(ProjectUrl));
        OpenReleaseUrlCommand = new RelayCommand(() =>
            OpenUrl(_latestRelease is { Url.Length: > 0 } release ? release.Url : ReleasesUrl));
        CheckUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync(), () => !_isChecking);
    }

    public string AppName => "AIMonitor 2.0";
    public string AppVersion { get; }
    public string DeveloperName => "Apichart Chantanis";

    public string DeveloperBlurb =>
        "AI Usage Monitor reads the AI sign-ins you already have on this machine and shows what each service says "
        + "you have used. It never writes to another tool's credentials and never sends your usage anywhere.";

    public string AssistantsBlurb =>
        "Written and maintained by the author above, with help from two coding assistants: Claude Code (Anthropic) and "
        + "Codex (OpenAI). They are tools, in the same sense as the compiler; the design decisions and the releases are "
        + "the author's.";

    public string LicenseFooter =>
        $"Version {AppVersion}  ·  GPL-3.0-or-later. The full licence is under About > License Agreement, and the notices "
        + "for redistributed components sit beside it.";

    public string CheckButtonText => _isChecking ? "Checking…" : "Check for updates";

    public string ReleaseButtonText => _isUpdateAvailable ? "Get the update" : "Open releases page";

    public string NoticesText => ReadBesideApp("THIRD-PARTY-NOTICES.md")
        ?? "The notices for redistributed components ship beside the application (THIRD-PARTY-NOTICES.md).";

    private static string? ReadBesideApp(string fileName)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
    public string DeveloperEmail => "apichart@apichart.net";
    public string ProjectUrl => "https://github.com/epinephrinerx/AIMonitor";

    public string LicenseText => ReadBesideApp("LICENSE.txt") ?? LicenseExcerpt;

    private const string LicenseExcerpt =
        """
        GNU GENERAL PUBLIC LICENSE
        Version 3, 29 June 2007

        Copyright (C) 2026 Apichart Chantanis

        Everyone is permitted to copy and distribute verbatim copies
        of this license document, but changing it is not allowed.

        This program is free software: you can redistribute it and/or modify
        it under the terms of the GNU General Public License as published by
        the Free Software Foundation, either version 3 of the License, or
        (at your option) any later version.

        This program is distributed in the hope that it will be useful,
        but WITHOUT ANY WARRANTY; without even the implied warranty of
        MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
        GNU General Public License for more details.
        """;

    public string ReadmeText =>
        """
        AIMonitor 2.0 - AI Quota & Usage Monitor for Windows Desktop

        A native Windows application to monitor quotas and track token usage across AI providers:
        - Claude (Claude Code login, session/weekly limits, CLI transcript ingestion)
        - OpenAI (Codex ChatGPT OAuth, Admin API spend/budget, usage history)
        - Google Gemini (Cloud Monitoring request count, ADC, service account)

        Key Features:
        - Native WPF desktop dashboard and compact floating widget
        - Live Windows system tray icon with severity coloring
        - Windows user-bound credential protection (DPAPI)
        - Zero invented data and read-only external tool integration
        """;

    public string UpdateStatus
    {
        get => _updateStatus;
        set => SetProperty(ref _updateStatus, value);
    }

    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (SetProperty(ref _isChecking, value))
            {
                OnPropertyChanged(nameof(CheckButtonText));
                (CheckUpdatesCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (SetProperty(ref _isUpdateAvailable, value))
            {
                OnPropertyChanged(nameof(ReleaseButtonText));
            }
        }
    }

    public ReleaseInfo? LatestRelease
    {
        get => _latestRelease;
        set => SetProperty(ref _latestRelease, value);
    }

    public ICommand OpenProjectUrlCommand { get; }
    public ICommand OpenReleaseUrlCommand { get; }
    public ICommand CheckUpdatesCommand { get; }

    /// <summary>On demand only, never on open: one read-only request to GitHub.</summary>
    public async Task CheckUpdatesAsync()
    {
        IsChecking = true;
        UpdateStatus = "Asking GitHub for the newest release…";

        try
        {
            var release = await _versionChecker.CheckLatestAsync(AppVersion);
            LatestRelease = release;
            if (release.IsNewer)
            {
                IsUpdateAvailable = true;
                UpdateStatus = $"Update available: {release.Tag}{Published(release)}. You have {AppVersion}.";
            }
            else
            {
                IsUpdateAvailable = false;
                UpdateStatus = $"You are up to date. The newest release is {release.Tag}{Published(release)}.";
            }
        }
        catch (UpdateCheckException ex)
        {
            UpdateStatus = ex.Message;
            IsUpdateAvailable = false;
        }
        catch (Exception ex)
        {
            UpdateStatus = $"Update check failed: {ex.Message}";
            IsUpdateAvailable = false;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static string Published(ReleaseInfo release) =>
        string.IsNullOrEmpty(release.Published) ? "" : $" (published {release.Published})";

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Non-fatal if browser cannot be launched
        }
    }
}
