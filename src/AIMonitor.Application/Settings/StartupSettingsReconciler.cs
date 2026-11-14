using AIMonitor.Application.Windows;

namespace AIMonitor.Application.Settings;

/// <summary>Applies a fresh-profile default once; afterwards Windows intent wins. The installer owns upgrade path changes.</summary>
public static class StartupSettingsReconciler
{
    public static async Task ReconcileAsync(
        SettingsSession session,
        IStartupRegistrar registrar,
        string executablePath,
        bool firstLaunch = false,
        bool preserveInstallerIntent = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(registrar);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        cancellationToken.ThrowIfCancellationRequested();
        var state = registrar.GetRegistrationState();
        if (state is null) return;
        var enabled = state.Value;
        // A portable launch must never steal an existing installed startup command.
        if (!enabled && firstLaunch && !preserveInstallerIntent && session.Current.StartWithWindows)
        {
            registrar.Register(executablePath);
            state = registrar.GetRegistrationState();
            if (state is null) return;
            enabled = state.Value;
        }

        if (session.Current.StartWithWindows != enabled)
        {
            await session.UpdateAsync(s => s with { StartWithWindows = enabled }, cancellationToken).ConfigureAwait(false);
        }
    }
}
