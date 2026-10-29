using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Persistence;

/// <summary>
/// <see cref="IOnboardingState"/> over the singleton <c>onboarding_state</c> row (id = 1). A fresh
/// database has <c>completed = 0</c>, so <see cref="IsComplete"/> is false on first run.
/// </summary>
public sealed class SqliteOnboardingState : IOnboardingState
{
    private readonly ISqliteConnectionFactory _factory;

    public SqliteOnboardingState(ISqliteConnectionFactory factory) => _factory = factory;

    public bool IsComplete => Get().IsComplete;

    public OnboardingStatus Get()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT completed, current_step, started_utc, completed_utc FROM onboarding_state WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new OnboardingStatus(false, null, null, null);
        }

        return new OnboardingStatus(
            IsComplete: reader.GetInt64(0) != 0,
            CurrentStep: reader.IsDBNull(1) ? null : reader.GetString(1),
            StartedUtc: reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            CompletedUtc: reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public void SetCurrentStep(string step)
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE onboarding_state SET current_step = $step, " +
            "started_utc = COALESCE(started_utc, $now) WHERE id = 1;";
        command.Parameters.AddWithValue("$step", step);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void MarkComplete()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE onboarding_state SET completed = 1, completed_utc = $now, " +
            "started_utc = COALESCE(started_utc, $now) WHERE id = 1;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Reset()
    {
        using var connection = _factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE onboarding_state SET completed = 0, completed_utc = NULL WHERE id = 1;";
        command.ExecuteNonQuery();
    }
}
