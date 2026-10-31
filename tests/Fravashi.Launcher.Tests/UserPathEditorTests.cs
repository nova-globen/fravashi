using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class UserPathEditorTests
{
    private const string Dir = @"C:\Users\me\AppData\Local\Programs\Fravashi";

    [Fact]
    public void Remove_TakesOutEveryCopy_ByTheSameComparison_AndKeepsTheRestInOrder()
    {
        // Backlog 375: the uninstall's PATH step.
        var (newPath, changed) = UserPathEditor.Remove(@"C:\bin;" + Dir + @"\;C:\tools;" + Dir.ToUpperInvariant(), Dir);

        changed.Should().BeTrue();
        newPath.Should().Be(@"C:\bin;C:\tools");
    }

    [Fact]
    public void Remove_WhenAbsent_ChangesNothing()
    {
        var (newPath, changed) = UserPathEditor.Remove(@"C:\bin;;C:\tools", Dir);

        changed.Should().BeFalse();
        newPath.Should().Be(@"C:\bin;;C:\tools", "an empty segment someone left is not ours to tidy");
    }

    [Fact]
    public void Remove_OfTheOnlyEntry_LeavesAnEmptyPath()
    {
        UserPathEditor.Remove(Dir, Dir).Should().Be((string.Empty, true));
    }

    [Fact]
    public void Ensure_AppendsToEmptyPath()
    {
        var (newPath, changed) = UserPathEditor.Ensure("", Dir);

        changed.Should().BeTrue();
        newPath.Should().Be(Dir);
    }

    [Fact]
    public void Ensure_AppendsWhenAbsent()
    {
        var (newPath, changed) = UserPathEditor.Ensure(@"C:\bin;C:\tools", Dir);

        changed.Should().BeTrue();
        newPath.Should().Be($@"C:\bin;C:\tools;{Dir}");
    }

    [Fact]
    public void Ensure_IsIdempotent_WhenAlreadyPresent()
    {
        var current = $@"C:\bin;{Dir};C:\tools";

        var (newPath, changed) = UserPathEditor.Ensure(current, Dir);

        changed.Should().BeFalse();
        newPath.Should().Be(current);
    }

    [Fact]
    public void Ensure_IgnoresTrailingSeparatorDifferences()
    {
        var current = $@"C:\bin;{Dir}\"; // present, but with a trailing backslash

        var (_, changed) = UserPathEditor.Ensure(current, Dir);

        changed.Should().BeFalse();
    }

    [Fact]
    public void Ensure_IsCaseInsensitive()
    {
        var current = Dir.ToUpperInvariant();

        var (_, changed) = UserPathEditor.Ensure(current, Dir);

        changed.Should().BeFalse();
    }

    [Fact]
    public void Ensure_DoesNotIntroduceDoubleSemicolon_WhenPathEndsInSeparator()
    {
        var (newPath, changed) = UserPathEditor.Ensure(@"C:\bin;", Dir);

        changed.Should().BeTrue();
        newPath.Should().Be($@"C:\bin;{Dir}");
    }

    [Fact]
    public void Apply_WritesOnlyWhenChanged()
    {
        var stored = @"C:\bin";
        var writes = 0;

        var first = UserPathEditor.Apply(() => stored, v => { stored = v; writes++; }, Dir);
        var second = UserPathEditor.Apply(() => stored, v => { stored = v; writes++; }, Dir);

        first.Should().BeTrue();
        second.Should().BeFalse();   // idempotent - the second Apply is a no-op
        writes.Should().Be(1);
        stored.Should().Be($@"C:\bin;{Dir}");
    }
}
