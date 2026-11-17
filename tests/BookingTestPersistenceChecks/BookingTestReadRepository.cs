using System.Data;
using Npgsql;

// Test-only, explicitly constructed read slice. Not an MVC service or a
// production adapter. It accepts only verified synthetic Customer sessions.
internal sealed class BookingTestReadRepository
{
    private readonly SyntheticCustomerConnections connections;
    internal BookingTestReadRepository(SyntheticCustomerConnections connections) =>
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));

    internal async Task<BookingTestReadSnapshot?> FindOwnAsync(SyntheticBookingSessions.VerifiedCustomer customer, Guid bookingId, CancellationToken cancellationToken = default)
    {
        var customerId = customer?.CustomerId ?? throw new InvalidOperationException("Verified synthetic Customer required.");
        if (bookingId == Guid.Empty) throw new InvalidOperationException("Invalid booking identifier.");
        BookingTestProtectedBoundary.RequireImplemented();
        using var operation = await customer.AcquireOperationAsync(cancellationToken);
        if (customer.CustomerId != customerId) throw new InvalidOperationException("Synthetic identity changed.");
        var login = connections.ExpectedLogin(customer);
        await using var connection = connections.Create(customer);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            await using (var guard = new NpgsqlCommand("""
                SELECT current_database()='glam_auth_mvc_workflow_test'
                   AND current_user=@login AND session_user=@login
                   AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@login AND rolcanlogin AND NOT rolsuper AND NOT rolbypassrls)
                   AND pg_has_role(current_user,'booking_test_runtime','USAGE')
                   AND booking_test.authenticated_fixture_customer()=@actor
                   AND (SELECT count(*)=2 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                        WHERE n.nspname='booking_test' AND c.relname IN ('bookings','booking_addons')
                          AND c.relrowsecurity AND c.relforcerowsecurity AND NOT pg_has_role(current_user,c.relowner,'MEMBER'))
                   AND inet_server_addr()='127.0.0.1'::inet
                   AND inet_client_addr()='127.0.0.1'::inet
                   AND inet_server_port()=5432
                   AND EXISTS (SELECT 1 FROM public.auth_mvc_test_sentinel WHERE name='glam_auth_mvc_workflow_test')
                   AND to_regclass('booking_test.bookings') IS NOT NULL
                   AND to_regclass('booking_test.booking_addons') IS NOT NULL
                   AND to_regclass('booking_test.services') IS NOT NULL
                   AND to_regclass('booking_test.addons') IS NOT NULL
                    AND to_regclass('booking_test.settings') IS NOT NULL
                    AND to_regclass('booking_test.synthetic_customers') IS NOT NULL
                """, connection, transaction))
            {
                guard.Parameters.AddWithValue("login", login);
                guard.Parameters.AddWithValue("actor", customerId);
                if ((bool?)await guard.ExecuteScalarAsync(cancellationToken) != true)
                    throw new InvalidOperationException("The isolated booking test schema is unavailable or unsafe.");
            }

            await BookingTestDatabaseSessionRepository.LockAsync(connection, transaction, customer, cancellationToken);
            await BookingTestCreateRepository.EnsureCustomerAsync(connection, transaction, customer, cancellationToken);

            await using var query = new NpgsqlCommand("""
                SELECT b.id,b.customer_id,b.service_name,b.people,b.package,b.agreed_total,
                       b.minimum_due,b.start_at,b.end_at,b.reserved_until,b.buffer_minutes,b.status,
                       b.service_id,b.service_price,b.service_minutes,b.group_price,b.group_minimum,
                       b.applied_price,b.original_total,b.discount,b.extras_total,b.down_percent,b.address,b.area
                FROM booking_test.bookings b
                WHERE b.id=@booking AND b.customer_id=@customer AND b.snapshot_sealed
                """, connection, transaction);
            query.Parameters.AddWithValue("booking", bookingId);
            query.Parameters.AddWithValue("customer", customerId);
            BookingTestReadSnapshot snapshot;
            await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) { _ = customer.CustomerId; return null; }
                snapshot = new BookingTestReadSnapshot(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetString(4), reader.GetDecimal(5), reader.GetDecimal(6),
                    reader.GetDateTime(7), reader.GetDateTime(8), reader.GetDateTime(9), reader.GetInt32(10), reader.GetString(11),
                    reader.GetGuid(12), reader.GetDecimal(13), reader.GetInt32(14), reader.IsDBNull(15) ? null : reader.GetDecimal(15),
                    reader.GetInt32(16), reader.GetDecimal(17), reader.GetDecimal(18), reader.GetDecimal(19), reader.GetDecimal(20),
                    reader.GetInt32(21), reader.GetString(22), reader.GetString(23), []);
            }
            await using var lines = new NpgsqlCommand("""
                SELECT a.addon_id,a.name_snapshot,a.price_snapshot,a.minutes_snapshot,a.quantity
                FROM booking_test.booking_addons a JOIN booking_test.bookings b ON b.id=a.booking_id
                WHERE b.id=@booking AND b.customer_id=@customer ORDER BY a.addon_id
                """, connection, transaction);
            lines.Parameters.AddWithValue("booking", bookingId);
            lines.Parameters.AddWithValue("customer", customerId);
            var addons = new List<BookingTestAddonSnapshot>();
            await using (var lineReader = await lines.ExecuteReaderAsync(cancellationToken))
                while (await lineReader.ReadAsync(cancellationToken))
                    addons.Add(new(lineReader.GetGuid(0), lineReader.GetString(1), lineReader.GetDecimal(2), lineReader.GetInt32(3), lineReader.GetInt32(4)));
            await BookingTestDatabaseSessionRepository.LockAsync(connection, transaction, customer, cancellationToken);
            _ = customer.CustomerId;
            return snapshot with { Addons = addons };
        }
        catch (NpgsqlException)
        {
            // Do not reveal connection strings, server detail or SQL to callers.
            throw new InvalidOperationException("The isolated booking test database is unavailable or incompatible.");
        }
    }
}

internal sealed record BookingTestReadSnapshot(Guid Id, Guid CustomerId, string ServiceName, int People,
    string Package, decimal AgreedTotal, decimal MinimumDue, DateTime StartUtc, DateTime EndUtc,
    DateTime ReservedUntilUtc, int BufferMinutes, string Status, Guid ServiceId, decimal ServicePrice,
    int ServiceMinutes, decimal? GroupPrice, int GroupMinimum, decimal AppliedPrice, decimal OriginalTotal,
    decimal Discount, decimal ExtrasTotal, int DownPercent, string Address, string Area, IReadOnlyList<BookingTestAddonSnapshot> Addons);
internal sealed record BookingTestAddonSnapshot(Guid Id, string Name, decimal Price, int Minutes, int Quantity);
