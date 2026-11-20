using System.Diagnostics;
using System.Text;
using AIMonitor.Infrastructure;
using AIMonitor.Infrastructure.MacOS;
using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.MacOSSmoke;

/// <summary>
/// Drives the real macOS adapters against the real operating system, which the unit tests cannot:
/// they replace <c>security</c> and <c>launchctl</c> with fakes so they can run anywhere.
///
/// Everything it touches is isolated from the installed app — its own Keychain service, its own
/// LaunchAgent label, its own socket directory — and it removes what it creates, so running it
/// never disturbs the user's settings, keys or login items.
///
/// Run it with: dotnet run --project tools/AIMonitor.MacOSSmoke
/// </summary>
internal static class Program
{
    private const string SmokeKeychainService = "AIMonitor2.secrets.smoke";
    private const string SmokeKeychainAccount = "smoke-key";
    private const string SmokeLaunchAgentLabel = "net.apichart.aiusagemonitor.smoke";
    private const string SyntheticSecret = "not-a-real-credential";

    private static int _failures;

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--secondary", var socketPath, .. var forwarded])
        {
            return await RunSecondaryAsync(socketPath, forwarded).ConfigureAwait(false);
        }

        Console.OutputEncoding = new UTF8Encoding(false);
        Section("Platform");
        Report("runs on a platform with a full set of adapters", PlatformAdapters.IsSupportedPlatform);
        Console.WriteLine($"  os                   {Environment.OSVersion.VersionString} ({(OperatingSystem.IsMacOS() ? "macOS" : "not macOS")})");
        Console.WriteLine($"  architecture         {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"  app data             {PlatformAdapters.AppDataDirectory}");
        Console.WriteLine($"  secret protector     {PlatformAdapters.CreateSecretProtector().GetType().Name}");
        Console.WriteLine($"  startup registrar    {PlatformAdapters.CreateStartupRegistrar().GetType().Name}");
        await using (var coordinator = PlatformAdapters.CreateSingleInstanceCoordinator())
        {
            Console.WriteLine($"  single instance      {coordinator.GetType().Name}");
        }

        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine();
            Console.WriteLine("This tool exercises the macOS adapters, so there is nothing to run here.");
            return 0;
        }

        await RunKeychainCheckAsync().ConfigureAwait(false);
        RunLaunchAgentCheck();
        await RunSingleInstanceCheckAsync().ConfigureAwait(false);
        RunClaudeLoginCheck();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "All checks passed." : $"{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static async Task RunKeychainCheckAsync()
    {
        Section("Login Keychain (service " + SmokeKeychainService + ")");
        var directory = Path.Combine(Path.GetTempPath(), $"aim-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "secrets.dat");
        var protector = new KeychainSecretProtector(SmokeKeychainService, SmokeKeychainAccount, MacOSCommand.Run);

        try
        {
            var store = new DpapiSecretStore(path, protector, new AtomicFileWriter());
            Console.WriteLine("  macOS may ask you to allow access to the Keychain; that prompt is the point.");

            await store.SetAsync("smoke/provider", SyntheticSecret).ConfigureAwait(false);
            Report("the sealed file exists", File.Exists(path));

            var onDisk = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
            Report(
                "the sealed file does not contain the plaintext",
                !Encoding.UTF8.GetString(onDisk).Contains(SyntheticSecret, StringComparison.Ordinal));

            var readBack = await store.GetAsync("smoke/provider").ConfigureAwait(false);
            Report("the value reads back unchanged", readBack == SyntheticSecret);

            var reopened = new DpapiSecretStore(
                path,
                new KeychainSecretProtector(SmokeKeychainService, SmokeKeychainAccount, MacOSCommand.Run),
                new AtomicFileWriter());
            Report(
                "a second store reads it with the key from the Keychain",
                await reopened.GetAsync("smoke/provider").ConfigureAwait(false) == SyntheticSecret);

            Report("the key can be removed again", protector.Forget());
        }
        finally
        {
            protector.Forget();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RunLaunchAgentCheck()
    {
        Section("LaunchAgent (label " + SmokeLaunchAgentLabel + ")");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var registrar = new LaunchAgentStartupRegistrar(
            home, SmokeLaunchAgentLabel, MacOSCommand.Run, () => $"gui/{GetUserId()}");

        try
        {
            Report("nothing is registered to begin with", registrar.GetRegistrationState() == false);

            registrar.Register("/Applications/AI Usage Monitor.app/Contents/MacOS/AIUsageMonitor", "--tray");
            Console.WriteLine($"  wrote                {registrar.PlistPath}");
            Report("the plist was written", File.Exists(registrar.PlistPath));
            Report("it reads back as registered", registrar.IsRegistered());

            var lint = MacOSCommand.Run(
                MacOSCommand.StartInfo("/usr/bin/plutil", "-lint", registrar.PlistPath), null);
            Report("plutil accepts the plist", lint.Succeeded, lint.StandardOutput + lint.StandardError);

            var arguments = LaunchAgentPlist.ReadProgramArguments(File.ReadAllText(registrar.PlistPath));
            Report("the arguments survived the round trip", arguments is ["/Applications/AI Usage Monitor.app/Contents/MacOS/AIUsageMonitor", "--tray"]);

            registrar.Unregister();
            Report("turning it off removes the plist", !File.Exists(registrar.PlistPath));
            Report("and it reads back as not registered", registrar.GetRegistrationState() == false);

            var stillLoaded = MacOSCommand.Run(
                MacOSCommand.StartInfo("/bin/launchctl", "print", $"gui/{GetUserId()}/{SmokeLaunchAgentLabel}"), null);
            Report("launchd never loaded it in the first place", !stillLoaded.Succeeded);
        }
        finally
        {
            if (File.Exists(registrar.PlistPath)) File.Delete(registrar.PlistPath);
        }
    }

    private static async Task RunSingleInstanceCheckAsync()
    {
        Section("Single instance across two processes");
        var directory = Path.Combine(Path.GetTempPath(), $"aim-si-{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(directory);
        var socketPath = Path.Combine(directory, "si.sock");

        try
        {
            await using var primary = LocalSocketSingleInstance.ForSocketPath(socketPath, TimeSpan.FromSeconds(3));
            Report("the first process takes ownership", await primary.TryAcquireAsync().ConfigureAwait(false));

            var activated = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            primary.Activated += arguments => activated.TrySetResult(arguments);

            var executable = Environment.ProcessPath;
            if (executable is null)
            {
                Report("this process knows its own executable path", false);
                return;
            }

            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var argument in new[] { "--secondary", socketPath, "--tray" })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var secondary = Process.Start(startInfo)!;
            await secondary.WaitForExitAsync().ConfigureAwait(false);

            Report("the second process exits cleanly instead of opening a window", secondary.ExitCode == 0,
                $"exit code {secondary.ExitCode}");

            var received = await activated.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Report("the first process is told to show itself, with the arguments", received is ["--tray"]);
            Report("the first process still owns the app", primary.HasOwnership);
        }
        catch (TimeoutException)
        {
            Report("the first process is told to show itself, with the arguments", false, "no activation arrived");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Where this machine keeps the Claude Code login, if anywhere.
    ///
    /// Presence only: the query carries no <c>-w</c>, so it reads the item's attributes and never
    /// its contents. That is what keeps it from raising the Keychain's "allow access?" prompt, and
    /// it means this check never sees a token.
    /// </summary>
    private static void RunClaudeLoginCheck()
    {
        Section("Claude Code login");
        var file = Path.Combine(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
            ".credentials.json");

        var fileExists = File.Exists(file);
        Console.WriteLine($"  file                 {file}");
        Console.WriteLine($"  file present         {(fileExists ? "yes" : "no")}");

        var probe = MacOSCommand.Run(
            MacOSKeychain.BuildServiceProbeStartInfo(ClaudeKeychainCredentialReader.KeychainService), null);
        var inKeychain = probe.Succeeded;
        Console.WriteLine($"  Keychain item        {(inKeychain ? "present" : "absent")} "
            + $"(service \"{ClaudeKeychainCredentialReader.KeychainService}\")");

        Report(
            "a Claude Code login exists in one of the two places the app looks",
            fileExists || inKeychain,
            "sign in by running `claude` in a terminal, then run this again");

        if (!fileExists && inKeychain)
        {
            Console.WriteLine("        The login is in the Keychain only, which is the macOS norm — a build");
            Console.WriteLine("        that looked for the file alone would report 'not signed in' here.");
        }
    }

    /// <summary>The second launch: refuse to start, hand the arguments over, exit 0.</summary>
    private static async Task<int> RunSecondaryAsync(string socketPath, string[] forwarded)
    {
        await using var coordinator = LocalSocketSingleInstance.ForSocketPath(socketPath, TimeSpan.FromSeconds(3));
        if (await coordinator.TryAcquireAsync().ConfigureAwait(false))
        {
            Console.Error.WriteLine("secondary: took ownership, which means the guard did not work");
            return 2;
        }

        return await coordinator.NotifyExistingInstanceAsync(forwarded).ConfigureAwait(false) ? 0 : 3;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }

    private static void Report(string what, bool passed, string? detail = null)
    {
        if (!passed) _failures++;
        Console.WriteLine($"  {(passed ? "PASS" : "FAIL")}  {what}");
        if (!passed && !string.IsNullOrWhiteSpace(detail))
        {
            Console.WriteLine($"        {detail.Trim()}");
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetUserId();
}
