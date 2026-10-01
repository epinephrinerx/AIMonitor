using System.Security;
using AIMonitor.Infrastructure.Windows;

namespace AIMonitor.Infrastructure.Tests.Windows;

public sealed class WindowsStartupRegistrarTests
{
    [Fact]
    public void DefaultValueName_MatchesLegacyParity()
    {
        var registrar = new WindowsStartupRegistrar();
        Assert.Equal("AIUsageMonitor", registrar.ValueName);
    }

    [Fact]
    public void IsRegistered_WhenKeyNotFound_ReturnsFalse()
    {
        var registrar = new WindowsStartupRegistrar("TestApp", _ => null);
        Assert.False(registrar.IsRegistered());
    }

    [Fact]
    public void IsRegistered_WhenValueMissing_ReturnsFalse()
    {
        var fakeKey = new FakeWritableKey();
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        Assert.False(registrar.IsRegistered());
    }

    [Fact]
    public void IsRegistered_WhenValueEmpty_ReturnsFalse()
    {
        var fakeKey = new FakeWritableKey();
        fakeKey.Values["TestApp"] = "   ";
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        Assert.False(registrar.IsRegistered());
    }

    [Fact]
    public void IsRegistered_WhenValueExists_ReturnsTrue()
    {
        var fakeKey = new FakeWritableKey();
        fakeKey.Values["TestApp"] = "\"C:\\Apps\\AIMonitor\\AIMonitor.exe\"";
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        Assert.True(registrar.IsRegistered());
    }

    [Fact]
    public void Register_QuotesExecutablePath()
    {
        var fakeKey = new FakeWritableKey();
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        registrar.Register(@"C:\Apps\AIMonitor\AIMonitor.exe");

        Assert.True(fakeKey.Values.ContainsKey("TestApp"));
        Assert.Equal("\"C:\\Apps\\AIMonitor\\AIMonitor.exe\"", fakeKey.Values["TestApp"]);
    }

    [Fact]
    public void Register_WithArguments_QuotesPathAndAppendsArguments()
    {
        var fakeKey = new FakeWritableKey();
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        registrar.Register(@"C:\Apps\AIMonitor\AIMonitor.exe", "--minimized --tray");

        Assert.Equal("\"C:\\Apps\\AIMonitor\\AIMonitor.exe\" --minimized --tray", fakeKey.Values["TestApp"]);
    }

    [Fact]
    public void Register_WhenInputAlreadyHasQuotes_DoesNotDoubleQuote()
    {
        var fakeKey = new FakeWritableKey();
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        registrar.Register("\"C:\\Apps\\AIMonitor\\AIMonitor.exe\"", "--tray");

        Assert.Equal("\"C:\\Apps\\AIMonitor\\AIMonitor.exe\" --tray", fakeKey.Values["TestApp"]);
    }

    [Fact]
    public void Register_WhenKeyCannotBeOpened_ThrowsInvalidOperationException()
    {
        var registrar = new WindowsStartupRegistrar("TestApp", _ => null);

        var ex = Assert.Throws<InvalidOperationException>(() => registrar.Register(@"C:\Apps\AIMonitor.exe"));
        Assert.Contains("Cannot open registry key", ex.Message);
    }

    [Fact]
    public void Register_WhenSecurityException_WrapsInInvalidOperationException()
    {
        var fakeKey = new FakeWritableKey { ThrowOnSet = new SecurityException("Permission denied") };
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        var ex = Assert.Throws<InvalidOperationException>(() => registrar.Register(@"C:\Apps\AIMonitor.exe"));
        Assert.Contains("Access denied", ex.Message);
        Assert.IsType<SecurityException>(ex.InnerException);
    }

    [Fact]
    public void Unregister_WhenValueExists_RemovesValue()
    {
        var fakeKey = new FakeWritableKey();
        fakeKey.Values["TestApp"] = "\"C:\\Apps\\AIMonitor.exe\"";
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        registrar.Unregister();

        Assert.False(fakeKey.Values.ContainsKey("TestApp"));
    }

    [Fact]
    public void Unregister_WhenValueDoesNotExist_DoesNotThrow()
    {
        var fakeKey = new FakeWritableKey();
        var registrar = new WindowsStartupRegistrar("TestApp", _ => fakeKey);

        var exception = Record.Exception(() => registrar.Unregister());
        Assert.Null(exception);
    }

    [Fact]
    public void Unregister_WhenKeyCannotBeOpened_DoesNotThrow()
    {
        var registrar = new WindowsStartupRegistrar("TestApp", _ => null);

        var exception = Record.Exception(() => registrar.Unregister());
        Assert.Null(exception);
    }

    private sealed class FakeWritableKey : WindowsStartupRegistrar.IWritableRegistryKey
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? ThrowOnSet { get; set; }
        public bool IsDisposed { get; private set; }

        public string? GetStringValue(string name) =>
            Values.TryGetValue(name, out var val) ? val : null;

        public void SetStringValue(string name, string value)
        {
            if (ThrowOnSet is not null) throw ThrowOnSet;
            Values[name] = value;
        }

        public void DeleteValue(string name)
        {
            Values.Remove(name);
        }

        public bool HasValue(string name) => Values.ContainsKey(name);

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
