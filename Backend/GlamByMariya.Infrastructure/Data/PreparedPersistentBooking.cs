using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

// Activation requires reviewed schema, trigger, authorization and concurrency verification.
internal static class PreparedPersistentBooking
{
    internal static bool Enabled => false;

    internal static List<CatalogItem> Resolve(Guid[] ids, int people, IReadOnlyList<CatalogItem> catalog)
    {
        if (people is < 1 or > BookingPricing.MaximumPeople || ids.Length > (long)catalog.Count * people)
            throw new InvalidOperationException("Choose valid add-on quantities.");
        var groups = ids.GroupBy(id => id).ToList();
        if (groups.Any(g => g.Count() > people)) throw new InvalidOperationException("Add-on quantity cannot exceed the number of people.");
        return groups.SelectMany(g => Enumerable.Repeat(catalog.FirstOrDefault(a => a.Id == g.Key && a.Active && !a.Archived)
            ?? throw new InvalidOperationException("An add-on is no longer available."), g.Count())).ToList();
    }

    internal static void ValidateClaim(decimal amount, BookingQuote quote)
    {
        Money.RequireExactDeposit(amount, quote.RequiredDownPayment);
    }

    // Pure preparation only: callers must obtain actor and catalog mapping from
    // trusted, owner-scoped reads in the same database snapshot.
    internal static IReadOnlyList<MixedPersonQuote> ReconstructMixed(
        AppUser actor, MixedHeader header, Guid verifiedBridalServiceId,
        IReadOnlyList<MixedPersonRow> people, IReadOnlyList<MixedAddonRow> addons)
    {
        static InvalidOperationException Invalid() => new("Mixed booking snapshot is unavailable.");
        if (actor == null || !actor.IsActive || actor.Id == Guid.Empty || header.BookingId == Guid.Empty ||
            header.OwnerId == Guid.Empty || !header.Sealed || verifiedBridalServiceId == Guid.Empty ||
            !(actor.Role == Roles.SuperAdmin || (actor.Role == Roles.Customer && actor.Id == header.OwnerId)) ||
            header.People is < 2 or > BookingPricing.MaximumPeople || people == null || addons == null ||
            people.Count != header.People)
            throw Invalid();
        var ordered = people.OrderBy(p => p.Position).ToArray();
        if (ordered.Select((p, i) => p.BookingId == header.BookingId && p.Position == i + 1 &&
                p.ServiceId != Guid.Empty && p.IsBridal == (p.ServiceId == verifiedBridalServiceId) &&
                !string.IsNullOrWhiteSpace(p.Name) && p.Price > 0 && Money.Round(p.Price) == p.Price &&
                p.Minutes is >= 1 and <= 480).Any(valid => !valid) ||
            ordered.Count(p => p.IsBridal) > 1 ||
            addons.Any(a => a.BookingId != header.BookingId || a.Position < 1 || a.Position > header.People ||
                a.AddonId == Guid.Empty || string.IsNullOrWhiteSpace(a.Name) || a.Price < 0 ||
                Money.Round(a.Price) != a.Price || a.Minutes is < 0 or > 480) ||
            addons.GroupBy(a => (a.Position, a.AddonId)).Any(g => g.Count() != 1))
            throw Invalid();
        try
        {
            if (ordered.Sum(p => p.Price) != header.ServiceSubtotal ||
                addons.Sum(a => a.Price) != header.AddonSubtotal ||
                ordered.Sum(p => p.Minutes) != header.ServiceMinutes ||
                addons.Sum(a => a.Minutes) != header.AddonMinutes ||
                checked(header.ServiceMinutes + header.AddonMinutes) > 1440)
                throw Invalid();
            return Array.AsReadOnly(ordered.Select(p => {
                var selected = addons.Where(a => a.Position == p.Position).OrderBy(a => a.AddonId)
                    .Select(a => new PriceSnapshot(a.AddonId, a.Name, a.Price, a.Minutes)).ToArray();
                return new MixedPersonQuote(new PriceSnapshot(p.ServiceId, p.Name, p.Price, p.Minutes),
                    Array.AsReadOnly(selected), p.Price + selected.Sum(a => a.Price),
                    checked(p.Minutes + selected.Sum(a => a.Minutes)));
            }).ToArray());
        }
        catch (OverflowException) { throw Invalid(); }
    }

    internal sealed record MixedHeader(Guid BookingId, Guid OwnerId, bool Sealed, int People,
        decimal ServiceSubtotal, decimal AddonSubtotal, int ServiceMinutes, int AddonMinutes);
    internal sealed record MixedPersonRow(Guid BookingId, int Position, Guid ServiceId, bool IsBridal, string Name, decimal Price, int Minutes);
    internal sealed record MixedAddonRow(Guid BookingId, int Position, Guid AddonId, string Name, decimal Price, int Minutes);

    internal static Dictionary<Guid, List<PriceSnapshot>> ReadLines(MakeupDbContext db, Guid[] bookingIds, bool publicSchema)
    {
        if (!Enabled) throw new InvalidOperationException("Prepared persistent booking storage is not enabled.");
        if (bookingIds.Length == 0) return [];
        var sql = publicSchema ? """
            SELECT booking_id AS "BookingId", addon_id AS "AddonId", name_snapshot AS "Name",
                   price_snapshot AS "Price", minutes_snapshot AS "Minutes", quantity AS "Quantity"
            FROM public.mvc_booking_addons WHERE booking_id = ANY(@ids)
            """ : """
            SELECT booking_id AS "BookingId", addon_id AS "AddonId", name_snapshot AS "Name",
                   price_snapshot AS "Price", minutes_snapshot AS "Minutes", quantity AS "Quantity"
            FROM makeup.booking_addons WHERE booking_id = ANY(@ids)
            """;
        var rows = db.Database.SqlQueryRaw<PreparedLine>(sql, new NpgsqlParameter("ids", bookingIds)).ToList();
        return rows.GroupBy(r => r.BookingId).ToDictionary(g => g.Key,
            g => g.Select(r => new PriceSnapshot(r.AddonId, r.Name, r.Price, r.Minutes, r.Quantity)).ToList());
    }

    internal sealed class PreparedLine
    {
        public Guid BookingId { get; set; }
        public Guid AddonId { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public int Minutes { get; set; }
        public int Quantity { get; set; }
    }
}
