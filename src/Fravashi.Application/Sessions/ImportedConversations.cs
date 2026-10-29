using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>One conversation file the CLI wrote on the host for the active workspace (backlog 43).</summary>
public sealed record CliConversationFile(string Name, long Size, DateTimeOffset ModifiedUtc);

/// <summary>What reading a conversation file came back as.</summary>
public enum CliConversationReadStatus
{
    Read,
    Missing,
    TooLarge,
}

/// <summary>A conversation file's bytes, or why there are none.</summary>
public sealed record CliConversationContent(CliConversationReadStatus Status, byte[] Bytes)
{
    public static readonly CliConversationContent Missing = new(CliConversationReadStatus.Missing, []);

    public static readonly CliConversationContent TooLarge = new(CliConversationReadStatus.TooLarge, []);
}

/// <summary>
/// Where conversations run OUTSIDE the studio come from (backlog 43): the host CLI's own history for the
/// ACTIVE workspace, read through the bridge. Read-only by construction: there is nothing here that writes.
/// A studio with no bridge workspace lists nothing, never a folder of its own.
/// </summary>
public interface ICliConversationSource
{
    Task<IReadOnlyList<CliConversationFile>> ListAsync(CancellationToken cancellationToken);

    Task<CliConversationContent> ReadAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Why an imported conversation shows no transcript, when it shows none.</summary>
public enum ImportedConversationProblem
{
    None,

    /// <summary>The file is over the bridge's import cap, so it was never read.</summary>
    TooLarge,

    /// <summary>The file was listed but was gone by the time it was read.</summary>
    Missing,
}

/// <summary>
/// A conversation imported from the CLI's own history: text only. Tool calls, token counts and cost are
/// not in what was recovered, and nothing on this record pretends otherwise.
/// </summary>
public sealed record ImportedConversation(
    string WorkspaceId,
    string Name,
    string Title,
    DateTimeOffset StartedUtc,
    DateTimeOffset ModifiedUtc,
    long Size,
    IReadOnlyList<MessageEmitted> Messages,
    ImportedConversationProblem Problem = ImportedConversationProblem.None)
{
    /// <summary>The CLI's own session id: the file name without its extension.</summary>
    public string SessionId => Name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? Name[..^6] : Name;
}
