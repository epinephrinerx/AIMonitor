using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using AIMonitor.Application.Windows;

namespace AIMonitor.Infrastructure.MacOS;

/// <summary>
/// The macOS counterpart of <c>WindowsStartupRegistrar</c>: a LaunchAgent plist in
/// <c>~/Library/LaunchAgents</c> is the entry System Settings lists under Login Items, so the user's
/// choice there stays visible to the app — the same property that made the per-user Run key the right
/// mechanism on Windows (PAR-024). Neither needs admin rights.
/// </summary>
internal sealed class LaunchAgentStartupRegistrar : IStartupRegistrar
{
    private readonly string _homeDirectory;
    private readonly string _label;
    private readonly Func<ProcessStartInfo, string?, CommandResult> _run;
    private readonly Func<string> _sessionTarget;

    public LaunchAgentStartupRegistrar()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            LaunchAgentPlist.DefaultLabel,
            MacOSCommand.Run,
            CurrentGuiSessionTarget)
    {
    }

    internal LaunchAgentStartupRegistrar(
        string homeDirectory,
        string label,
        Func<ProcessStartInfo, string?, CommandResult> run,
        Func<string> sessionTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        _homeDirectory = homeDirectory;
        _label = label;
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _sessionTarget = sessionTarget ?? throw new ArgumentNullException(nameof(sessionTarget));
    }

    public string Label => _label;

    public string PlistPath => LaunchAgentPlist.PathFor(_homeDirectory, _label);

    public bool IsRegistered() => GetRegistrationState() ?? false;

    public bool? GetRegistrationState()
    {
        try
        {
            if (!File.Exists(PlistPath))
            {
                return false;
            }

            // A plist we cannot parse is not an entry we can claim is on, but it is not proof the
            // user turned it off either.
            if (LaunchAgentPlist.ReadProgramArguments(File.ReadAllText(PlistPath)) is null)
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Null, not false: the caller must preserve the saved intent rather than read an
            // unreadable file as "the user turned it off".
            return null;
        }

        return !IsDisabledInLoginItems();
    }

    public void Register(string executablePath, string arguments = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        try
        {
            Directory.CreateDirectory(LaunchAgentPlist.DirectoryFor(_homeDirectory));
            File.WriteAllText(
                PlistPath,
                LaunchAgentPlist.Build(_label, executablePath, arguments),
                new UTF8Encoding(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot write the login item at '{PlistPath}'.", exception);
        }
    }

    /// <summary>
    /// Deletes the plist and nothing else.
    ///
    /// Deliberately no <c>launchctl bootout</c>: when this very process was started by the agent,
    /// booting the agent out kills the app the user is standing in. Removing the file is enough —
    /// the entry is gone from the next login onwards, which is what the setting promises.
    /// </summary>
    public void Unregister()
    {
        try
        {
            File.Delete(PlistPath);
        }
        catch (DirectoryNotFoundException)
        {
            // No ~/Library/LaunchAgents at all, so there is no entry to remove. File.Delete is a
            // no-op for a missing file but throws for a missing directory.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Cannot remove the login item at '{PlistPath}'.", exception);
        }
    }

    private bool IsDisabledInLoginItems()
    {
        try
        {
            var startInfo = MacOSCommand.StartInfo(
                LaunchAgentPlist.LaunchctlPath, "print-disabled", _sessionTarget());
            var result = _run(startInfo, null);
            return result.Succeeded && LaunchAgentPlist.IsDisabledByUser(result.StandardOutput, _label);
        }
        catch (Exception exception) when (exception
            is PlatformNotSupportedException
            or InvalidOperationException
            or TimeoutException
            or IOException)
        {
            // Best effort: any surprise from launchctl means the file alone decides.
            return false;
        }
    }

    /// <summary>The launchctl domain holding this user's GUI session state.</summary>
    internal static string CurrentGuiSessionTarget() => $"gui/{GetEffectiveUserId()}";

    /// <summary>
    /// The caller's uid, which names the GUI domain launchctl keeps per-user state in. There is no
    /// managed API for it, and spawning <c>id -u</c> to learn our own identity would be absurd.
    /// </summary>
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
