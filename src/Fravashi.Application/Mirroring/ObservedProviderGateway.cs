using Fravashi.Application.Network;

namespace Fravashi.Application.Mirroring;

/// <summary>
/// A provider gateway whose every answer is reported to <see cref="ProviderOutages"/> (backlog 232), so "Jira unreachable
/// since 14:02" is learned from the calls the product makes anyway and never from a probe of its own. It passes every
/// call and every result through unchanged: what the outbox decides from a result is not this class's business.
/// </summary>
public sealed class ObservedProviderGateway(IProviderGateway inner, ProviderOutages outages) : IProviderGateway
{
    /// <inheritdoc />
    public string Connection => inner.Connection;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities => inner.Capabilities;

    /// <inheritdoc />
    public Task<ProviderResult<ProviderItem>> CreateAsync(string payload, string operationId, CancellationToken ct = default) =>
        SeenAsync(inner.CreateAsync(payload, operationId, ct));

    /// <inheritdoc />
    public Task<ProviderResult<ProviderItem>> UpdateAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
        SeenAsync(inner.UpdateAsync(key, payload, providerVersion, operationId, ct));

    /// <inheritdoc />
    public Task<ProviderResult<ProviderItem>> TransitionAsync(
        string key, string payload, string? providerVersion, string operationId, CancellationToken ct = default) =>
        SeenAsync(inner.TransitionAsync(key, payload, providerVersion, operationId, ct));

    /// <inheritdoc />
    public Task<ProviderResult<ProviderItem?>> FindByOperationIdAsync(string operationId, CancellationToken ct = default) =>
        SeenAsync(inner.FindByOperationIdAsync(operationId, ct));

    /// <inheritdoc />
    public Task<ProviderResult<ProviderItem>> GetAsync(string key, CancellationToken ct = default) =>
        SeenAsync(inner.GetAsync(key, ct));

    /// <inheritdoc />
    public Task<ProviderResult<ProviderPage>> ListAsync(string? cursor, CancellationToken ct = default) =>
        SeenAsync(inner.ListAsync(cursor, ct));

    /// <inheritdoc />
    public Task<ProviderResult<IReadOnlyList<string>>> OfferedTransitionsAsync(string key, CancellationToken ct = default) =>
        SeenAsync(inner.OfferedTransitionsAsync(key, ct));

    private async Task<ProviderResult<T>> SeenAsync<T>(Task<ProviderResult<T>> call)
    {
        var result = await call.ConfigureAwait(false);
        outages.Observe(inner.Connection, result.Network);
        return result;
    }
}
