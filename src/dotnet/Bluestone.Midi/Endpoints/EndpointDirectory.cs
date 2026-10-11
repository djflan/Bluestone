namespace Bluestone.Midi.Endpoints;

/// <summary>
/// All endpoint providers available to a session, presented as one. Opening is dispatched to the
/// provider named in <see cref="EndpointId.Provider"/>.
/// </summary>
public sealed class EndpointDirectory : IDisposable
{
    private readonly IReadOnlyList<IMidiEndpointProvider> _providers;

    public EndpointDirectory(IEnumerable<IMidiEndpointProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = [.. providers];
        if (_providers.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != _providers.Count)
        {
            throw new ArgumentException("Provider IDs must be unique.", nameof(providers));
        }

        foreach (var provider in _providers)
        {
            provider.EndpointsChanged += OnProviderChanged;
        }
    }

    public IReadOnlyList<IMidiEndpointProvider> Providers => _providers;

    /// <summary>Raised on an arbitrary thread when any provider's endpoints change.</summary>
    public event EventHandler? EndpointsChanged;

    public IReadOnlyList<EndpointDescriptor> GetEndpoints() => [.. _providers.SelectMany(p => p.GetEndpoints())];

    public IReadOnlyList<EndpointDescriptor> GetEndpoints(EndpointDirection direction) =>
        [.. GetEndpoints().Where(e => e.Direction == direction)];

    /// <exception cref="EndpointUnavailableException">No provider owns the endpoint, or it cannot be opened.</exception>
    public ValueTask<IMidiOutput> OpenOutputAsync(EndpointId id, CancellationToken cancellationToken = default) =>
        ProviderFor(id).OpenOutputAsync(id, cancellationToken);

    /// <exception cref="EndpointUnavailableException">No provider owns the endpoint, or it cannot be opened.</exception>
    public ValueTask<IMidiInput> OpenInputAsync(EndpointId id, CancellationToken cancellationToken = default) =>
        ProviderFor(id).OpenInputAsync(id, cancellationToken);

    /// <summary>Unsubscribes from providers. Providers are owned by the caller and are not disposed.</summary>
    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.EndpointsChanged -= OnProviderChanged;
        }
    }

    private IMidiEndpointProvider ProviderFor(EndpointId id) =>
        _providers.FirstOrDefault(p => p.Id == id.Provider)
        ?? throw new EndpointUnavailableException(id, $"no provider named '{id.Provider}' is available");

    private void OnProviderChanged(object? sender, EventArgs e) => EndpointsChanged?.Invoke(this, EventArgs.Empty);
}
