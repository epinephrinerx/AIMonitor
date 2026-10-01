using AIMonitor.Infrastructure.Storage;

namespace AIMonitor.Infrastructure.Tests.Storage;

public sealed class LegacyRegistrySettingsReaderTests
{
    [Fact]
    public void ReadAll_NestedSyntheticTree_FlattensValuesUsingQSettingsPathsAndOnlyReads()
    {
        var operations = new List<string>();
        var root = new FakeKey("root", operations)
            .Value("theme", "dark")
            .SubKey("widget", key => key.Value("opacity", "0.75"))
            .SubKey("providers", providers => providers.SubKey("openai", openAi =>
                openAi.Value("enabled", 1).Value("key", "synthetic-protected-blob")));
        var reader = new LegacyRegistrySettingsReader(() => root);

        var values = reader.ReadAll();

        Assert.Equal("dark", values["theme"]);
        Assert.Equal("0.75", values["widget/opacity"]);
        Assert.Equal(1, values["providers/openai/enabled"]);
        Assert.Equal("synthetic-protected-blob", values["providers/openai/key"]);
        Assert.All(operations, operation => Assert.True(
            operation.StartsWith("read:", StringComparison.Ordinal) ||
            operation.StartsWith("open:", StringComparison.Ordinal) ||
            operation.StartsWith("list:", StringComparison.Ordinal) ||
            operation.StartsWith("dispose:", StringComparison.Ordinal), operation));
        Assert.DoesNotContain(operations, operation => operation.StartsWith("write:", StringComparison.Ordinal));
        Assert.DoesNotContain(operations, operation => operation.StartsWith("delete:", StringComparison.Ordinal));
    }

    private sealed class FakeKey(string name, List<string> operations) : LegacyRegistrySettingsReader.IReadOnlyRegistryKey
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FakeKey> _subKeys = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> ValueNames
        {
            get { operations.Add($"list:values:{name}"); return _values.Keys; }
        }
        public IEnumerable<string> SubKeyNames
        {
            get { operations.Add($"list:subkeys:{name}"); return _subKeys.Keys; }
        }
        public object? GetValue(string valueName)
        {
            operations.Add($"read:{name}/{valueName}");
            return _values[valueName];
        }
        public LegacyRegistrySettingsReader.IReadOnlyRegistryKey? OpenSubKey(string subKeyName)
        {
            operations.Add($"open:{name}/{subKeyName}");
            return _subKeys.GetValueOrDefault(subKeyName);
        }
        public FakeKey Value(string valueName, object? value)
        {
            _values[valueName] = value;
            return this;
        }
        public FakeKey SubKey(string subKeyName, Action<FakeKey> configure)
        {
            var child = new FakeKey($"{name}/{subKeyName}", operations);
            configure(child);
            _subKeys[subKeyName] = child;
            return this;
        }
        public void Dispose() => operations.Add($"dispose:{name}");
    }
}
