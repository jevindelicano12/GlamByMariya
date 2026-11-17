using GlamByMariya.Core.Entities;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Not registered or selectable by configuration; paired with the reviewed schema gate.
internal static class PreparedPersistentProfile
{
    internal static void LoadOwnProfile(NpgsqlDataSource source, AppUser user, bool publicSchema)
    {
        if (!PreparedPersistentBooking.Enabled || user.Role != "Customer" || user.Id == Guid.Empty)
            throw new InvalidOperationException("Prepared persistent profile storage is not enabled.");
        using var connection = source.OpenConnection();
        using var command = new NpgsqlCommand(publicSchema
            ? "SELECT suffix, birthday FROM public.customers WHERE app_id=@id AND is_active"
            : "SELECT suffix, birthday FROM makeup.app_users WHERE id=@id AND is_active", connection);
        command.Parameters.AddWithValue("id", user.Id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The profile could not be verified.");
        user.Suffix = reader.IsDBNull(0) ? null : reader.GetString(0);
        user.Birthday = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateOnly>(1);
        if (reader.Read()) throw new InvalidOperationException("The profile could not be verified.");
    }
}
