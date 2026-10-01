using System.Runtime.Versioning;
using System.Security;
using AIMonitor.Application.Windows;
using Microsoft.Win32;

namespace AIMonitor.Infrastructure.Windows;

/// <summary>
/// Registers or unregisters the application in HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Conforms to PAR-024 and ADR-0003: startup entry reflects user intent and uses Windows as the source of truth.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistrar : IStartupRegistrar
{
    public const string RunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "AIUsageMonitor";

    private readonly string _valueName;
    private readonly Func<bool, IWritableRegistryKey?> _openRunKey;

    public WindowsStartupRegistrar()
        : this(DefaultValueName, OpenWindowsRunKey)
    {
    }

    public WindowsStartupRegistrar(string valueName)
        : this(valueName, OpenWindowsRunKey)
    {
    }

    internal WindowsStartupRegistrar(string valueName, Func<bool, IWritableRegistryKey?> openRunKey)
    {
        _valueName = string.IsNullOrWhiteSpace(valueName) ? DefaultValueName : valueName.Trim();
        _openRunKey = openRunKey ?? throw new ArgumentNullException(nameof(openRunKey));
    }

    public string ValueName => _valueName;

    public bool IsRegistered()
    {
        try
        {
            using var key = _openRunKey(false);
            if (key is null) return false;

            var value = key.GetStringValue(_valueName);
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void Register(string executablePath, string arguments = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var normalizedPath = Path.GetFullPath(executablePath.Trim().Trim('"'));
        var command = string.IsNullOrWhiteSpace(arguments)
            ? $"\"{normalizedPath}\""
            : $"\"{normalizedPath}\" {arguments.Trim()}";

        try
        {
            using var key = _openRunKey(true);
            if (key is null)
            {
                throw new InvalidOperationException($"Cannot open registry key 'HKCU\\{RunRegistrySubKey}' for writing.");
            }

            key.SetStringValue(_valueName, command);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Access denied when registering startup entry in 'HKCU\\{RunRegistrySubKey}'.", ex);
        }
    }

    public void Unregister()
    {
        try
        {
            using var key = _openRunKey(true);
            if (key is null) return;

            if (key.HasValue(_valueName))
            {
                key.DeleteValue(_valueName);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Access denied when unregistering startup entry in 'HKCU\\{RunRegistrySubKey}'.", ex);
        }
    }

    private static IWritableRegistryKey? OpenWindowsRunKey(bool writable)
    {
        var key = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, writable);
        return key is null ? null : new WindowsRegistryKeyWrapper(key);
    }

    internal interface IWritableRegistryKey : IDisposable
    {
        string? GetStringValue(string name);
        void SetStringValue(string name, string value);
        void DeleteValue(string name);
        bool HasValue(string name);
    }

    private sealed class WindowsRegistryKeyWrapper(RegistryKey key) : IWritableRegistryKey
    {
        private readonly RegistryKey _key = key ?? throw new ArgumentNullException(nameof(key));

        public string? GetStringValue(string name)
        {
            var val = _key.GetValue(name);
            return val as string;
        }

        public void SetStringValue(string name, string value)
        {
            _key.SetValue(name, value, RegistryValueKind.String);
        }

        public void DeleteValue(string name)
        {
            _key.DeleteValue(name, throwOnMissingValue: false);
        }

        public bool HasValue(string name)
        {
            return _key.GetValue(name) is not null;
        }

        public void Dispose()
        {
            _key.Dispose();
        }
    }
}
