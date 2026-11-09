using AIMonitor.Infrastructure.Providers.Claude;

namespace AIMonitor.Infrastructure.Tests.Providers.Claude;

/// <summary>
/// Covers SPEC R-014 "P2b detailed" §P2b.1 - <c>ResolveAll</c> turns the configured locations into the
/// list of <c>projects</c> directories the transcript store scans - together with the decisions that
/// override it: A11 (a blank <c>CLAUDE_CONFIG_DIR</c> is unset), B9 (an entry whose last segment is
/// <c>projects</c> is that directory already), B14 (a non-blank value with no usable entry is unset) and
/// B15 (an entry that is not a fully qualified path, or is drive-relative, is skipped).
/// </summary>
/// <remarks>
/// P2b.1 states the resolver is pure - "no environment or disk access" - so the tests pass invented paths
/// on drives that need not exist and never touch the machine's real configuration. B18: each kept entry
/// comes back in its original spelling (case as written), with <c>\projects</c> appended unless B9
/// applies, so expected and actual paths are compared through <see cref="Path.GetFullPath"/> ignoring
/// case (<see cref="StringComparer.OrdinalIgnoreCase"/>) - rule 3's own identity: the value of the result
/// is the directory it denotes, not the spelling used to write it.
/// The existing <c>Resolve(...)</c> (credentials path logic) is unchanged by P2b and stays covered by
/// <see cref="ClaudeTranscriptPathResolverTests"/>.
/// </remarks>
[Trait("Category", "Contract")]
public sealed class ClaudeTranscriptPathResolverResolveAllTests
{
    private const string Profile = @"C:\Users\dev";

    // The two default roots P2b.1 rule 2 specifies when CLAUDE_CONFIG_DIR is unset.
    private static readonly string DefaultXdgProjects = Path.Combine(Profile, ".config", "claude", "projects");
    private static readonly string DefaultDotClaudeProjects = Path.Combine(Profile, ".claude", "projects");

    private static IReadOnlyList<string> ResolveAll(string? claudeConfigDir, string? xdgConfigHome = null) =>
        ClaudeTranscriptPathResolver.ResolveAll(claudeConfigDir, xdgConfigHome, Profile);

    private static void AssertProjects(IReadOnlyList<string> actual, params string[] expectedProjectsDirectories) =>
        // B18: the result keeps the spelling the entry was written with, so the comparison sees through
        // to the directory it denotes - GetFullPath plus OrdinalIgnoreCase, exactly rule 3's identity.
        Assert.Equal(
            expectedProjectsDirectories.Select(Path.GetFullPath),
            actual.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);

    // -- rule 1: CLAUDE_CONFIG_DIR replaces the defaults ------------------------------------------

    [Fact]
    public void ResolveAll_TwoEntries_EachBecomesItsOwnProjectsRoot_AndDefaultsAreNotAdded()
    {
        var roots = ResolveAll(@"D:\claude-one,E:\claude-two");

        // P2b.1 rule 1: "each entry -> <entry>\projects. Defaults are not added."
        AssertProjects(roots, @"D:\claude-one\projects", @"E:\claude-two\projects");
        Assert.Equal(2, roots.Count);
    }

    [Fact]
    public void ResolveAll_SingleEntry_ReturnsOnlyThatProjectsRoot()
    {
        AssertProjects(ResolveAll(@"D:\claude"), @"D:\claude\projects");
    }

    [Fact]
    public void ResolveAll_TrimsEachEntryAndDropsEmptyEntries()
    {
        // P2b.1 rule 1: "split on `,`, trim each, drop empty entries".
        var roots = ResolveAll("  D:\\a  , ,, E:\\b ,,");

        AssertProjects(roots, @"D:\a\projects", @"E:\b\projects");
    }

    [Fact]
    public void ResolveAll_KeepsTheOrderOfTheEntries()
    {
        AssertProjects(
            ResolveAll(@"E:\third,D:\first,E:\second"),
            @"E:\third\projects",
            @"D:\first\projects",
            @"E:\second\projects");
    }

    [Theory]
    [InlineData(@"D:\claude\projects")]
    [InlineData(@"D:\Claude\PROJECTS")]
    [InlineData(@"D:\claude\projects\")]
    public void ResolveAll_EntryWhoseLastSegmentIsProjects_IsUsedAsTheProjectsDirectory(string entry)
    {
        // B9 (ccusage normalize_claude_config_path, done syntactically): no second \projects is appended.
        // B18: what comes back is the entry as written - "D:\Claude\PROJECTS" keeps its case - so the
        // comparison is case-insensitive. The trailing-separator case denotes the same directory, so all
        // three rows must give this one.
        AssertProjects(ResolveAll(entry), Path.Combine(@"D:\claude", "projects"));
    }

    // -- rule 2: the defaults ----------------------------------------------------------------------

    [Fact]
    public void ResolveAll_ConfigDirUnsetAndXdgProvided_UsesXdgClaudeThenProfileDotClaude()
    {
        var roots = ResolveAll(null, @"D:\xdg");

        // P2b.1 rule 2: "<xdg>\claude\projects ... then <profile>\.claude\projects".
        AssertProjects(roots, Path.Combine(@"D:\xdg", "claude", "projects"), DefaultDotClaudeProjects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAll_ConfigDirUnsetAndXdgUnset_UsesProfileDotConfigThenProfileDotClaude(string? xdgConfigHome)
    {
        // P2b.1 rule 2: "<xdg> = xdgConfigHome if non-blank else <profile>\.config".
        var roots = ResolveAll(null, xdgConfigHome);

        AssertProjects(roots, DefaultXdgProjects, DefaultDotClaudeProjects);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t , \t")]
    public void ResolveAll_BlankOrWhitespaceConfigDir_IsTreatedAsUnset(string? blankConfigDir)
    {
        // A11 overrides rule 1: "CLAUDE_CONFIG_DIR set but empty or whitespace-only -> treated as unset
        // (defaults are used)".
        AssertProjects(ResolveAll(blankConfigDir), DefaultXdgProjects, DefaultDotClaudeProjects);
    }

    [Theory]
    [InlineData(",")]
    [InlineData(",,")]
    [InlineData(", ,")]
    public void ResolveAll_ConfigDirThatYieldsNoEntries_IsTreatedAsUnset(string configDir)
    {
        // B14: "A CLAUDE_CONFIG_DIR that is non-blank but yields zero entries (e.g. \",\") is treated as
        // unset (like A11)."
        AssertProjects(ResolveAll(configDir), DefaultXdgProjects, DefaultDotClaudeProjects);
    }

    // -- B15: unusable entries are skipped ---------------------------------------------------------

    [Theory]
    [InlineData(@"sub\claude")]              // relative
    [InlineData(@".\claude")]                // relative
    [InlineData(@"..\claude")]               // relative
    [InlineData(@"%USERPROFILE%\.claude")]   // not expanded, and not rooted
    [InlineData(@"~\.claude")]               // not expanded, and not rooted
    [InlineData(@"C:x")]                     // drive-relative (same rule as R8)
    [InlineData(@"C:")]                      // drive-relative (same rule as R8)
    public void ResolveAll_SkipsUnqualifiedAndDriveRelativeEntries(string unusableEntry)
    {
        // B15: "an entry that is not a fully qualified path, or is drive-relative (C:x), is skipped (same
        // rule as R8)".
        var roots = ResolveAll(unusableEntry + @",D:\usable");

        AssertProjects(roots, @"D:\usable\projects");
    }

    [Theory]
    [InlineData(@"sub\claude,C:x,")]
    [InlineData(@"%USERPROFILE%\.claude")]
    public void ResolveAll_WhenNoEntryRemains_ItIsTreatedAsUnset(string configDir)
    {
        // B15: "...if no entry remains -> treated as unset".
        AssertProjects(ResolveAll(configDir), DefaultXdgProjects, DefaultDotClaudeProjects);
    }

    // -- rule 3: duplicate removal -----------------------------------------------------------------

    [Fact]
    public void ResolveAll_RemovesDuplicatesKeepingTheFirstOccurrenceAndTheOrder()
    {
        // P2b.1 rule 3 as fixed by B18: "Duplicates removed (Path.GetFullPath, StringComparer
        // OrdinalIgnoreCase), first occurrence kept, order kept". "d:\a\" and "D:\a" denote one directory,
        // as do "E:\b" and "e:\b\", and the kept entry keeps its original spelling - lower-case
        // "d:\a\projects" here - which is why AssertProjects compares after GetFullPath, ignoring case.
        var roots = ResolveAll(@"D:\b,d:\a\,D:\a,E:\b,e:\b\");

        AssertProjects(roots, @"D:\b\projects", @"d:\a\projects", @"E:\b\projects");
        Assert.Equal(3, roots.Count);
    }

    [Fact]
    public void ResolveAll_ConfigDirEntryMatchingADefaultPath_AppearsExactlyOnce()
    {
        // Rule 1 already keeps the defaults out, so an entry that points where a default would point is
        // listed once - and only as the entry, not as two roots.
        var roots = ResolveAll(Profile + @"\.claude");

        AssertProjects(roots, DefaultDotClaudeProjects);
        Assert.Single(roots);
    }

    [Fact]
    public void ResolveAll_EntriesNormalizingToTheSameDirectory_AreListedOnce()
    {
        // B18 dropped this test's old premise: the two default candidates of rule 2 can never denote one
        // directory ("<xdg>\claude\projects" ends its parent segment "claude", "<profile>\.claude\projects"
        // ends ".claude" - different names even after GetFullPath, ignoring case). Rule 3 still has to
        // catch duplicates beyond case, and the real spelling of that is entries a GetFullPath treats as
        // one directory: "D:\a\b\.." resolves to "D:\a", so the projects directories collide and only the
        // first occurrence - kept - is listed.
        var roots = ResolveAll(@"D:\a\b\..,D:\a");

        AssertProjects(roots, @"D:\a\projects");
        Assert.Single(roots);
    }

    // -- purity ------------------------------------------------------------------------------------

    [Fact]
    public void ResolveAll_DoesNotFilterOutPathsThatDoNotExist()
    {
        // "pure (no environment or disk access)": deciding whether a root exists is the store's job
        // (P2b.2), so a path nothing on this machine can resolve must still be returned unchanged.
        var absentEntry = Path.Combine(Path.GetTempPath(), "aimonitor-absent-claude-" + Guid.NewGuid().ToString("N"));
        var absentXdg = Path.Combine(Path.GetTempPath(), "aimonitor-absent-xdg-" + Guid.NewGuid().ToString("N"));

        AssertProjects(ResolveAll(absentEntry), Path.Combine(absentEntry, "projects"));
        AssertProjects(
            ResolveAll(null, absentXdg),
            Path.Combine(absentXdg, "claude", "projects"),
            DefaultDotClaudeProjects);
    }

    [Fact]
    public void ResolveAll_IsDeterministic_ForTheSameInputs()
    {
        var first = ResolveAll(@"D:\a,D:\b", @"E:\xdg");
        var second = ResolveAll(@"D:\a,D:\b", @"E:\xdg");

        Assert.Equal(first.Select(Path.GetFullPath), second.Select(Path.GetFullPath));
    }

    [Fact]
    public void ResolveAll_UsesThePassedUserProfileDirectory_NotTheCurrentUsers()
    {
        // The profile arrives as an argument (P2b.6: App.xaml.cs passes it), so a different profile means
        // different defaults - nothing is read from the environment.
        var other = @"C:\Users\someone-else";

        AssertProjects(
            ClaudeTranscriptPathResolver.ResolveAll(null, null, other),
            Path.Combine(other, ".config", "claude", "projects"),
            Path.Combine(other, ".claude", "projects"));
    }
}
