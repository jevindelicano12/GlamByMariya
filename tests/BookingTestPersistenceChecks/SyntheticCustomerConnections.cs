using Npgsql;

// Test-only host configuration. Each verified synthetic identity selects its
// own authenticated login. Pooling is disabled; no connection crosses users.
// A trusted host must supply separate secrets out of band, never via requests.
internal sealed class SyntheticCustomerConnections
{
    private readonly string first;
    private readonly string second;
    internal SyntheticCustomerConnections(bool enabled, string firstConnection, string secondConnection, string schema)
    {
        if (!enabled || schema != "booking_test") throw new InvalidOperationException("Isolated booking connections disabled or unsafe.");
        first = Sanitize(firstConnection, "booking_test_customer_a");
        second = Sanitize(secondConnection, "booking_test_customer_b");
        if (new NpgsqlConnectionStringBuilder(first).Password == new NpgsqlConnectionStringBuilder(second).Password)
            throw new InvalidOperationException("Customer credentials must be distinct.");
    }

    private static string Sanitize(string connectionString, string login)
    {
        NpgsqlConnectionStringBuilder supplied;
        try { supplied = new(connectionString); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid isolated booking connection."); }
        if (supplied.Host != "127.0.0.1" || supplied.Port != 5432 || supplied.Database != "glam_auth_mvc_workflow_test" ||
            supplied.Username != login || string.IsNullOrEmpty(supplied.Password))
            throw new InvalidOperationException("Unexpected isolated Customer connection.");
        return new NpgsqlConnectionStringBuilder { Host="127.0.0.1", Port=5432,
            Database="glam_auth_mvc_workflow_test", Username=login, Password=supplied.Password,
            Pooling=false, Timeout=5, CommandTimeout=10 }.ConnectionString;
    }

    internal string ExpectedLogin(SyntheticBookingSessions.VerifiedCustomer customer) => customer.CustomerId switch
    {
        var id when id == SyntheticBookingSessions.CustomerA => "booking_test_customer_a",
        var id when id == SyntheticBookingSessions.CustomerB => "booking_test_customer_b",
        _ => throw new InvalidOperationException("Unrecognized synthetic Customer.")
    };

    internal bool PoolingDisabled => !new NpgsqlConnectionStringBuilder(first).Pooling && !new NpgsqlConnectionStringBuilder(second).Pooling;

    internal NpgsqlConnection Create(SyntheticBookingSessions.VerifiedCustomer customer)
    {
        BookingTestProtectedBoundary.RequireImplemented();
        return new(ExpectedLogin(customer) == "booking_test_customer_a" ? first : second);
    }
}
