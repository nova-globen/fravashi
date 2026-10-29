namespace Fravashi.Application.Scratchpad;

/// <summary>
/// The person's scratchpad (backlog 67): one markdown text per install, kept in the database like everything
/// else of theirs, so it survives a restart and travels in a backup.
/// <para>
/// It is deliberately a string and not a model. The owner's specification made the TEXT the thing: copying out
/// of the scratchpad gives markdown, pasting markdown in keeps it, and a to-do list is just lines written
/// <c>- [ ]</c> and <c>- [x]</c>. Anything that parsed it into structure on the way in would have to render it
/// back out identically, which is exactly the round trip the design chose to avoid.
/// </para>
/// </summary>
public interface IScratchpadStore
{
    /// <summary>What was last saved, or an empty string when nothing ever was.</summary>
    string Read();

    /// <summary>Replaces the scratchpad's text. A null is treated as empty, never as "no change".</summary>
    void Save(string? markdown);
}
