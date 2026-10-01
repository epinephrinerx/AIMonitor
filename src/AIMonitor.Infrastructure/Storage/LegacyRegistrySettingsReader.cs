using AIMonitor.Application.Settings;
using Microsoft.Win32;

namespace AIMonitor.Infrastructure.Storage;

public sealed class LegacyRegistrySettingsReader : ILegacySettingsReader
{
    public const string LegacyPath = @"Software\AIUsageMonitor\AIUsageMonitor";
    private readonly Func<IReadOnlyRegistryKey?> _openRoot;

    public LegacyRegistrySettingsReader()
        : this(OpenWindowsRoot)
    {
    }

    internal LegacyRegistrySettingsReader(Func<IReadOnlyRegistryKey?> openRoot) =>
        _openRoot = openRoot ?? throw new ArgumentNullException(nameof(openRoot));

    public IReadOnlyDictionary<string, object?> ReadAll()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var root = _openRoot();
        if (root is not null) ReadRecursive(root, string.Empty, values);
        return values;
    }

    private static IReadOnlyRegistryKey? OpenWindowsRoot()
    {
        var key = Registry.CurrentUser.OpenSubKey(LegacyPath, writable: false);
        return key is null ? null : new WindowsReadOnlyRegistryKey(key);
    }

    private static void ReadRecursive(IReadOnlyRegistryKey key, string prefix, IDictionary<string, object?> destination)
    {
        foreach (var valueName in key.ValueNames)
        {
            var name = string.IsNullOrEmpty(prefix) ? valueName : $"{prefix}/{valueName}";
            destination[name.Replace('\\', '/')] = key.GetValue(valueName);
        }

        foreach (var subKeyName in key.SubKeyNames)
        {
            using var subKey = key.OpenSubKey(subKeyName);
            if (subKey is not null)
            {
                var prefixName = string.IsNullOrEmpty(prefix) ? subKeyName : $"{prefix}/{subKeyName}";
                ReadRecursive(subKey, prefixName, destination);
            }
        }
    }

    internal interface IReadOnlyRegistryKey : IDisposable
    {
        IEnumerable<string> ValueNames { get; }
        IEnumerable<string> SubKeyNames { get; }
        object? GetValue(string name);
        IReadOnlyRegistryKey? OpenSubKey(string name);
    }

    private sealed class WindowsReadOnlyRegistryKey(RegistryKey key) : IReadOnlyRegistryKey
    {
        public IEnumerable<string> ValueNames => key.GetValueNames();
        public IEnumerable<string> SubKeyNames => key.GetSubKeyNames();
        public object? GetValue(string name) =>
            key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        public IReadOnlyRegistryKey? OpenSubKey(string name)
        {
            var subKey = key.OpenSubKey(name, writable: false);
            return subKey is null ? null : new WindowsReadOnlyRegistryKey(subKey);
        }
        public void Dispose() => key.Dispose();
    }
}
