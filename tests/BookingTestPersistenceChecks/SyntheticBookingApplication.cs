// Isolated test boundary; never registered in the normal MVC application.
internal interface ISyntheticBookingStore
{
    Task<Guid> CreateAsync(SyntheticBookingSessions.VerifiedCustomer customer, BookingTestCreateRequest request, CancellationToken ct = default);
    Task<Guid?> FindOwnAsync(SyntheticBookingSessions.VerifiedCustomer customer, Guid bookingId, CancellationToken ct = default);
}

internal sealed class SyntheticBookingApplication(SyntheticBookingSessions sessions, ISyntheticBookingStore store)
{
    internal Task<Guid> CreateAsync(string? sessionToken, BookingTestCreateRequest request, CancellationToken ct = default) => store.CreateAsync(sessions.Verify(sessionToken), request, ct);
    internal Task<Guid?> FindOwnAsync(string? sessionToken, Guid bookingId, CancellationToken ct = default) => store.FindOwnAsync(sessions.Verify(sessionToken), bookingId, ct);
}

internal sealed class PostgresSyntheticBookingStore(BookingTestCreateRepository create, BookingTestReadRepository read) : ISyntheticBookingStore
{
    public Task<Guid> CreateAsync(SyntheticBookingSessions.VerifiedCustomer customer, BookingTestCreateRequest request, CancellationToken ct = default) => create.CreateAsync(customer, request, ct);
    public async Task<Guid?> FindOwnAsync(SyntheticBookingSessions.VerifiedCustomer customer, Guid bookingId, CancellationToken ct = default) =>
        (await read.FindOwnAsync(customer, bookingId, ct))?.Id;
}

// This deliberately cannot assert anything about Npgsql or SQL authorization.
internal sealed class FakeSyntheticBookingStore : ISyntheticBookingStore
{
    private readonly object gate = new();
    private readonly FakeBookingTransactions transactions = new();
    private readonly Dictionary<Guid, Guid> owners = [];

    public Task<Guid> CreateAsync(SyntheticBookingSessions.VerifiedCustomer customer, BookingTestCreateRequest request, CancellationToken ct = default)
    {
        lock (gate)
        {
            var actor = customer.CustomerId;
            var hash = BookingTestCreateRepository.ValidateAndHash(actor, request);
            var id = transactions.Create(actor, request.SubmissionId, hash, request.StartUtc, request.StartUtc.AddMinutes(90));
            owners[id] = actor;
            return Task.FromResult(id);
        }
    }

    public Task<Guid?> FindOwnAsync(SyntheticBookingSessions.VerifiedCustomer customer, Guid bookingId, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult<Guid?>(owners.TryGetValue(bookingId, out var owner) && owner == customer.CustomerId ? bookingId : null);
    }
}
