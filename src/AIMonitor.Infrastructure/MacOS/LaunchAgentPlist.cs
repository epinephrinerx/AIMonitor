using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AIMonitor.Infrastructure.MacOS;

/// <summary>
/// Builds the LaunchAgent property list that opens the app at login, and the paths and commands
/// around it. Pure text work, so every rule below is testable on any host.
/// </summary>
internal static class LaunchAgentPlist
{
    /// <summary>
    /// The same label the 1.x macOS port used. 2.0 replaces 1.x in place rather than installing
    /// beside it (ADR-0003), so it must claim the same login item instead of adding a second one.
    /// </summary>
    public const string DefaultLabel = "net.apichart.aiusagemonitor";

    public const string LaunchctlPath = "/bin/launchctl";

    private const string DocTypeName = "plist";
    private const string DocTypePublicId = "-//Apple//DTD PLIST 1.0//EN";
    private const string DocTypeSystemId = "http://www.apple.com/DTDs/PropertyList-1.0.dtd";

    /// <summary><c>~/Library/LaunchAgents</c>, the only agent directory writable without admin rights.</summary>
    public static string DirectoryFor(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        return Path.Combine(homeDirectory, "Library", "LaunchAgents");
    }

    public static string PathFor(string homeDirectory, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return Path.Combine(DirectoryFor(homeDirectory), $"{label}.plist");
    }

    /// <summary>
    /// Splits the single argument string the <c>IStartupRegistrar</c> contract carries into the array
    /// launchd wants. A plain split on whitespace: the app's own switches (<c>--minimized</c>,
    /// <c>--tray</c>) never contain spaces, and inventing a quoting dialect here would differ from
    /// the one Windows already applies to the same string.
    /// </summary>
    public static string[] SplitArguments(string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? []
            : arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The plist contents for launching <paramref name="executablePath"/> at login.</summary>
    public static string Build(string label, string executablePath, string? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var programArguments = new XElement("array",
            new XElement("string", executablePath.Trim().Trim('"')));
        foreach (var argument in SplitArguments(arguments))
        {
            programArguments.Add(new XElement("string", argument));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType(DocTypeName, DocTypePublicId, DocTypeSystemId, null),
            new XElement("plist",
                new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"),
                    new XElement("string", label),
                    new XElement("key", "ProgramArguments"),
                    programArguments,
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"),
                    // Aqua only: this is a windowed app, and letting it load into a background
                    // session would start a copy with no display attached.
                    new XElement("key", "LimitLoadToSessionType"),
                    new XElement("string", "Aqua"))));

        // Written through a byte stream, not a StringWriter: a StringWriter is UTF-16, so the
        // declaration would claim utf-16 while the file on disk is UTF-8, and Apple's parser
        // rejects that mismatch outright.
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "\t",
            Encoding = new UTF8Encoding(false),
        };

        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, settings))
        {
            document.Save(writer);
        }

        return settings.Encoding.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>Reads <c>ProgramArguments</c> back out of a plist, or null if it is not one we wrote.</summary>
    public static string[]? ReadProgramArguments(string plistXml)
    {
        if (string.IsNullOrWhiteSpace(plistXml)) return null;

        XDocument document;
        try
        {
            document = XDocument.Parse(plistXml);
        }
        catch (XmlException)
        {
            return null;
        }

        var dictionary = document.Root?.Element("dict");
        if (dictionary is null) return null;

        var key = dictionary.Elements("key")
            .FirstOrDefault(element => element.Value == "ProgramArguments");
        if (key?.ElementsAfterSelf().FirstOrDefault() is not { Name.LocalName: "array" } array)
        {
            return null;
        }

        return [.. array.Elements("string").Select(element => element.Value)];
    }

    /// <summary>
    /// Whether <paramref name="printDisabledOutput"/> says the user switched this agent off.
    ///
    /// System Settings' Login Items writes a disabled override instead of deleting the plist, so the
    /// file on its own would report "on" for an agent macOS will never start. The rule that the
    /// system wins after first run is the same one the Windows side follows for the Run key.
    /// </summary>
    public static bool IsDisabledByUser(string? printDisabledOutput, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (string.IsNullOrEmpty(printDisabledOutput)) return false;

        var quoted = $"\"{label}\"";
        foreach (var line in printDisabledOutput.Split('\n'))
        {
            if (line.Contains(quoted, StringComparison.Ordinal))
            {
                return line.TrimEnd('\r', ' ', '\t').EndsWith("true", StringComparison.Ordinal);
            }
        }

        return false;
    }
}
