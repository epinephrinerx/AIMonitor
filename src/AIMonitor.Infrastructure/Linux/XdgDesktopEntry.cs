using System.Text;
using AIMonitor.Infrastructure.MacOS;

namespace AIMonitor.Infrastructure.Linux;

/// <summary>
/// Builds and reads XDG desktop entries for the per-user autostart directory, and the
/// <c>Exec=</c> value that goes into them. Pure text work with a path rule that uses
/// <c>/</c> regardless of host, so every method is testable on any OS.
/// </summary>
internal static class XdgDesktopEntry
{
    /// <summary>The <c>Name=</c> value written into every entry we create.</summary>
    public const string ApplicationName = "AI Usage Monitor";

    /// <summary>
    /// The 17 characters that force quoting around a single token in the <c>Exec=</c> value.
    /// <c>=</c> and <c>%</c> are deliberately absent: they survive unquoted in the Desktop Entry
    /// grammar, and <c>%</c> is instead doubled (A3(3)) so the parser does not treat it as a
    /// field-code introducer.
    /// </summary>
    private static bool NeedsQuoting(char c) => c is
        ' ' or '"' or '\'' or '\\' or '>' or '<' or '~' or '|'
        or '&' or ';' or '$' or '*' or '?' or '#'
        or '(' or ')' or '`';

    /// <summary>The four characters that receive a backslash escape inside a quoted token.</summary>
    private static bool NeedsBackslashEscape(char c) => c is '"' or '`' or '$' or '\\';

    /// <summary>The full <c>.desktop</c> contents for launching <paramref name="executablePath"/> at login.</summary>
    public static string Build(string executablePath, string? arguments = null)
    {
        return "[Desktop Entry]\n"
            + "Type=Application\n"
            + "Name=" + ApplicationName + "\n"
            + "Comment=Start AI Usage Monitor when you log in\n"
            + "Exec=" + FormatExec(executablePath, arguments) + "\n"
            + "Terminal=false\n"
            + "X-GNOME-Autostart-enabled=true\n";
    }

    /// <summary>
    /// Serialises the value that follows <c>Exec=</c> in a desktop entry. The rules (A2–A4) are
    /// a deliberate simplification: the spec guarantees only that the round-trip through this
    /// serialiser is what the test suite verifies, not that the resulting path runs.
    /// </summary>
    public static string FormatExec(string executablePath, string? arguments = null)
    {
        // A2(1): executablePath null/empty/whitespace → ArgumentException (null → ArgumentNullException).
        if (executablePath is null)
        {
            throw new ArgumentNullException(nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path must not be empty or whitespace.", nameof(executablePath));
        }

        // A2(2): control characters in either input are rejected before any trim or split, so a
        // path that merely *contains* a tab or newline cannot be sanitised into something valid.
        RejectControlCharacters(executablePath);
        if (arguments is not null)
        {
            RejectControlCharacters(arguments);
        }

        // A2(3): same trim-and-strip rule as LaunchAgentPlist.Build(:56).
        var executable = executablePath.Trim().Trim('"');

        // A2(4): the app is Linux-only at runtime; a relative path here is a caller bug (F13).
        if (executable.Length == 0 || executable[0] != '/')
        {
            throw new InvalidOperationException("Executable path must be absolute.");
        }

        // A2(5): reuse the same whitespace split the macOS side uses; there is no quoting dialect.
        var tokens = new List<string>(1 + 4);
        tokens.Add(executable);
        foreach (var argument in LaunchAgentPlist.SplitArguments(arguments))
        {
            tokens.Add(argument);
        }

        // A3: per-token quoting and %-doubling.
        for (var i = 0; i < tokens.Count; i++)
        {
            tokens[i] = FormatToken(tokens[i]);
        }

        // A4: join with one space, then escape backslashes at the Desktop Entry string level.
        return string.Join(' ', tokens).Replace("\\", "\\\\");
    }

    /// <summary>
    /// Parses the subset of the desktop-entry grammar the registrar cares about and reports
    /// whether the entry says the app should run. <see langword="null"/> means the contents do
    /// not describe a usable entry; callers must preserve existing intent in that case.
    /// </summary>
    public static bool? ReadEnabledState(string contents)
    {
        if (contents is null)
        {
            throw new ArgumentNullException(nameof(contents));
        }

        var foundDesktopEntry = false;
        string? lastExec = null;
        var hasExec = false;
        string? lastHidden = null;
        string? lastGnomeEnabled = null;
        var currentGroup = string.Empty;

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                // A7: a well-formed group header both opens and closes with brackets; a line
                // that only opens one is "broken" and leaves the current group unchanged.
                if (line.Length >= 2 && line[^1] == ']')
                {
                    currentGroup = line.Substring(1, line.Length - 2);
                    if (string.Equals(currentGroup, "Desktop Entry", StringComparison.Ordinal))
                    {
                        foundDesktopEntry = true;
                    }
                }

                continue;
            }

            // A7: only entries inside [Desktop Entry] count, every time it appears.
            if (!string.Equals(currentGroup, "Desktop Entry", StringComparison.Ordinal))
            {
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var key = line.Substring(0, equalsIndex).Trim();
            var value = line.Substring(equalsIndex + 1).Trim();

            switch (key)
            {
                case "Exec":
                    lastExec = value;
                    hasExec = true;
                    break;
                case "Hidden":
                    lastHidden = value;
                    break;
                case "X-GNOME-Autostart-enabled":
                    lastGnomeEnabled = value;
                    break;
            }
        }

        if (!foundDesktopEntry)
        {
            return null;
        }

        // A8: a missing or empty final Exec means the entry cannot launch anything we could
        // claim is registered.
        if (!hasExec || string.IsNullOrEmpty(lastExec))
        {
            return null;
        }

        // A8: Hidden true (last raw value wins, then interpreted) → off.
        if (ParseDesktopBoolean(lastHidden) == true)
        {
            return false;
        }

        // A8: X-GNOME-Autostart-enabled false → off.
        if (ParseDesktopBoolean(lastGnomeEnabled) == false)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Desktop-entry booleans are case-sensitive <c>true</c>/<c>1</c> or <c>false</c>/<c>0</c>;
    /// anything else is treated as if the key were absent (A8 "ค่าอื่น = ถือว่าไม่มี key").
    /// </summary>
    private static bool? ParseDesktopBoolean(string? value) => value switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null,
    };

    private static void RejectControlCharacters(string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F)
            {
                throw new InvalidOperationException(
                    $"Value contains a control character (0x{(int)c:X2}).");
            }
        }
    }

    /// <summary>Applies A3 to a single token: quote if needed, then double any <c>%</c>.</summary>
    private static string FormatToken(string token)
    {
        var needsQuote = false;
        foreach (var c in token)
        {
            if (NeedsQuoting(c))
            {
                needsQuote = true;
                break;
            }
        }

        string result;
        if (needsQuote)
        {
            // A3(2): escape " ` $ \ with a backslash, then wrap in double quotes.
            var sb = new StringBuilder(token.Length + 4);
            sb.Append('"');
            foreach (var c in token)
            {
                if (NeedsBackslashEscape(c))
                {
                    sb.Append('\\');
                }

                sb.Append(c);
            }

            sb.Append('"');
            result = sb.ToString();
        }
        else
        {
            result = token;
        }

        // A3(3): double every % so the parser does not read it as a field code.
        if (result.Contains('%'))
        {
            result = result.Replace("%", "%%");
        }

        return result;
    }
}
