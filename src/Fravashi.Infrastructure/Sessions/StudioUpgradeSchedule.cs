using System.Collections.Concurrent;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The upgrade a conversation asked for and is waiting on (backlog 157). In memory until the conversation's turn ends,
/// because that is when it is carried out; then on disk, under the data directory, because carrying it out replaces
/// this studio, and the studio that comes back has to know which conversation to pick up and why.
/// <para>
/// Only one upgrade at a time is ever scheduled: there is one product, and a second conversation asking for its own
/// is told which one is already waiting.
/// </para>
/// </summary>
public sealed class StudioUpgradeSchedule(string dataDirectory)
{
    /// <summary>Where the resume markers live, beside the database, so a container replacement keeps them.</summary>
    public const string FolderName = "upgrade-resume";

    private readonly ConcurrentDictionary<AgentSessionId, string> _scheduled = new();
    private readonly string _folder = Path.Combine(dataDirectory, FolderName);

    /// <summary>Schedules <paramref name="version"/> for the end of this conversation's turn. Null when scheduled, otherwise why not.</summary>
    public string? Schedule(AgentSessionId session, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        foreach (var (other, waiting) in _scheduled)
        {
            if (other != session)
            {
                return $"Another conversation already scheduled the upgrade to {waiting}; it runs when that conversation's turn ends.";
            }
        }

        _scheduled[session] = version;
        return null;
    }

    /// <summary>The version this conversation scheduled, taken off the schedule; null when it scheduled none.</summary>
    public string? Take(AgentSessionId session) => _scheduled.TryRemove(session, out var version) ? version : null;

    /// <summary>Records on disk that this conversation is to be resumed on <paramref name="version"/> once the studio is back.</summary>
    public void MarkResume(AgentSessionId session, string version)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(MarkerFor(session), version);
    }

    /// <summary>The version this conversation is to be resumed on, and the marker removed; null when there is none.</summary>
    public string? TakeResume(AgentSessionId session)
    {
        var path = MarkerFor(session);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var version = File.ReadAllText(path).Trim();
            File.Delete(path);
            return version.Length > 0 ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the marker, when the upgrade did not happen after all and the conversation simply goes on.</summary>
    public void ClearResume(AgentSessionId session)
    {
        try
        {
            File.Delete(MarkerFor(session));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A marker left behind is read once at the next start and resumes a conversation that was idle anyway.
        }
    }

    private string MarkerFor(AgentSessionId session) => Path.Combine(_folder, session.Value.ToString("N") + ".txt");
}
