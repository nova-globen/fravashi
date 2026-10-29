using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// Backlog 47/48. The board mapping is a setting a person fills in, and TWO things read it: the reconcile
/// pass, deciding what a provider's status means and what to send back, and the provider sync, deciding
/// what a move this board made is called over there.
/// <para>
/// This exists because of how the second one fails. A reader that is not wired does not throw and does not
/// log: it quietly reports a board nobody has mapped, and the product then asks the provider's workflow on
/// every drag instead of knowing the answer. That is indistinguishable from an install with no mapping,
/// which is a real state, so nothing downstream can tell the difference either. The registration is now a
/// single one both take, and this holds it that way.
/// </para>
/// </summary>
public sealed class StatusMappingWiringTests : IDisposable
{
    private readonly SqliteTestContext _context = new();

    public void Dispose() => _context.Dispose();

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISqliteConnectionFactory>(_context.Factory);
        services.AddSingleton<IConfigurationStore>(new SqliteConfigurationStore(_context.Factory));
        services.AddSingleton<IMirrorStore, Fravashi.Infrastructure.Mirroring.SqliteMirrorStore>();
        services.AddSingleton<IOutboxStore, Fravashi.Infrastructure.Mirroring.SqliteOutboxStore>();
        services.AddSingleton<Func<ProviderStatusMap>>(
            sp => () => StoredStatusMapping.Read(sp.GetRequiredService<IConfigurationStore>()));
        services.AddSingleton(sp => new ReconcilePass(
            sp.GetRequiredService<IMirrorStore>(),
            sp.GetRequiredService<IOutboxStore>(),
            sp.GetRequiredService<TimeProvider>(),
            newId: null,
            statuses: sp.GetRequiredService<Func<ProviderStatusMap>>()));
        return services.BuildServiceProvider();
    }

    /// <summary>Writes the mapping exactly where onboarding writes it, by the key both ends agree on.</summary>
    private static void Save(IConfigurationStore store, BoardStatusMapping mapping) =>
        store.Set(StoredStatusMapping.Key, System.Text.Json.JsonSerializer.Serialize(mapping));

    [Fact]
    public void A_mapping_saved_in_settings_is_what_the_registered_reader_answers_with()
    {
        using var provider = Build();
        var store = provider.GetRequiredService<IConfigurationStore>();
        Save(store, new BoardStatusMapping("jira",
        [
            new StatusMappingEntry("In Progress", "Being done"),
            new StatusMappingEntry("Done", "Shipped"),
            new StatusMappingEntry("Done", "Closed"),
        ]));

        var map = provider.GetRequiredService<Func<ProviderStatusMap>>()();

        map.Translate("Being done", null, WorkItemStatus.Backlog).How.Should().Be(StatusReading.Configured);
        map.ToNative(WorkItemStatus.InProgress).Should().Be("Being done");
        map.ReverseMap(WorkItemStatus.Done).Candidates.Should().Equal("Shipped", "Closed");
    }

    [Fact]
    public void The_reader_is_re_read_at_every_use_so_editing_the_mapping_takes_effect_at_once()
    {
        using var provider = Build();
        var store = provider.GetRequiredService<IConfigurationStore>();
        var read = provider.GetRequiredService<Func<ProviderStatusMap>>();
        Save(store, new BoardStatusMapping("jira", [new StatusMappingEntry("Done", "Shipped")]));
        read().ToNative(WorkItemStatus.Done).Should().Be("Shipped");

        Save(store, new BoardStatusMapping("jira", [new StatusMappingEntry("Done", "Released")]));

        read().ToNative(WorkItemStatus.Done).Should().Be("Released", "a settings edit is not waiting for a restart");
    }

    [Fact]
    public void An_install_with_no_mapping_is_a_real_state_and_not_an_error()
    {
        using var provider = Build();

        var map = provider.GetRequiredService<Func<ProviderStatusMap>>()();

        map.ReverseMap(WorkItemStatus.Done).How.Should().Be(ReverseReading.Unmapped);
        map.ToNative(WorkItemStatus.Done).Should().BeNull("nothing here says what that board calls it");
    }
}
