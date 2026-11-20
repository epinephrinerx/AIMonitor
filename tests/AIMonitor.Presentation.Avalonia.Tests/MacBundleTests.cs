using System.Xml.Linq;

namespace AIMonitor.Presentation.Avalonia.Tests;

/// <summary>
/// The macOS bundle is assembled by a shell script from a property-list template. Nothing compiles
/// either of them, so these read both as text and check the handful of facts that have to agree —
/// the executable name the script renames the binary to, the version it substitutes, and the two
/// documents GPL-3.0 requires the bundle to carry.
/// </summary>
public sealed class MacBundleTests
{
    private static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIMonitor.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
        }
    }

    private static string Template => File.ReadAllText(Path.Combine(RepositoryRoot, "build", "macos", "Info.plist.template"));

    private static string Script => File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", "build_macos_app.sh"));

    [Fact]
    public void TheTemplateIsAPropertyList()
    {
        var document = XDocument.Parse(Template.Replace(
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">",
            string.Empty));

        Assert.Equal("plist", document.Root!.Name.LocalName);
    }

    [Theory]
    [InlineData("CFBundleIdentifier")]
    [InlineData("CFBundleExecutable")]
    [InlineData("CFBundleName")]
    [InlineData("CFBundleShortVersionString")]
    [InlineData("CFBundleVersion")]
    [InlineData("CFBundlePackageType")]
    [InlineData("LSMinimumSystemVersion")]
    [InlineData("NSHighResolutionCapable")]
    [InlineData("NSHumanReadableCopyright")]
    public void TheTemplateCarriesTheKeysMacOSNeeds(string key)
    {
        Assert.Contains($"<key>{key}</key>", Template, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExecutableNameInThePlistIsTheOneTheScriptRenamesTheBinaryTo()
    {
        var declared = Value("CFBundleExecutable");

        // A mismatch here produces a bundle the Finder refuses to open, with no useful message.
        Assert.Contains($"MacOS/{declared}\"", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVersionIsSubstitutedRatherThanWrittenTwice()
    {
        Assert.Equal("__VERSION__", Value("CFBundleShortVersionString"));
        Assert.Equal("__VERSION__", Value("CFBundleVersion"));
        // Read from the one place the whole solution takes its version from (PAR-035).
        Assert.Contains("VersionPrefix", Script, StringComparison.Ordinal);
        Assert.Contains("__VERSION__", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCopyrightNamesTheLicence()
    {
        Assert.Contains("GPL-3.0", Value("NSHumanReadableCopyright"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LICENSE")]
    [InlineData("THIRD-PARTY-NOTICES.md")]
    public void TheScriptCopiesTheDocumentsTheLicenceRequires(string document)
    {
        Assert.Contains(document, Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundleIsBuiltAsADirectory_NeverAsASingleFile()
    {
        // A single-file bundle unpacks itself into a temporary directory on every launch, which an
        // app that opens at login would pay for every morning.
        Assert.Contains("-p:PublishSingleFile=false", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBundleIsSelfContained_SoNoRuntimeHasToBeInstalledFirst()
    {
        Assert.Contains("--self-contained true", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScriptSaysPlainlyThatItNeitherSignsWithADeveloperIdNorNotarizes()
    {
        Assert.Contains("NOT signed with a Developer ID", Script, StringComparison.Ordinal);
        Assert.Contains("NOT notarized", Script, StringComparison.Ordinal);
        Assert.DoesNotContain("notarytool", Script, StringComparison.Ordinal);
    }

    private static string Value(string key)
    {
        var marker = $"<key>{key}</key>";
        var index = Template.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{key} is missing from the template.");

        var rest = Template[(index + marker.Length)..];
        var open = rest.IndexOf('>', rest.IndexOf('<', StringComparison.Ordinal));
        var close = rest.IndexOf("</", open, StringComparison.Ordinal);
        return rest[(open + 1)..close].Trim();
    }
}
