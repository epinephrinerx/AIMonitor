using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using AIMonitor.Application.Version;

namespace AIMonitor.Presentation.Wpf.ViewModels;

public sealed class AboutViewModel : ViewModelBase
{
    private readonly IVersionChecker _versionChecker;
    private string _updateStatus = "Checking for updates...";
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
        {
            if (_latestRelease is not null)
            {
                OpenUrl(_latestRelease.Url);
            }
        });

        CheckUpdatesAsync();
    }

    public string AppName => "AIMonitor 2.0";
    public string AppVersion { get; }
    public string DeveloperName => "Apichart Chantanis";
    public string DeveloperEmail => "apichart@apichart.net";
    public string ProjectUrl => "https://github.com/epinephrinerx/AIMonitor";

    public string LicenseText =>
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
        set => SetProperty(ref _isChecking, value);
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set => SetProperty(ref _isUpdateAvailable, value);
    }

    public ReleaseInfo? LatestRelease
    {
        get => _latestRelease;
        set => SetProperty(ref _latestRelease, value);
    }

    public ICommand OpenProjectUrlCommand { get; }
    public ICommand OpenReleaseUrlCommand { get; }

    public async void CheckUpdatesAsync()
    {
        IsChecking = true;
        UpdateStatus = "Checking for updates...";

        try
        {
            var release = await _versionChecker.CheckLatestAsync(AppVersion);
            LatestRelease = release;
            if (release.IsNewer)
            {
                IsUpdateAvailable = true;
                UpdateStatus = $"New version {release.Tag} is available! (Published: {release.Published})";
            }
            else
            {
                IsUpdateAvailable = false;
                UpdateStatus = $"You are running the latest version ({AppVersion}).";
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
