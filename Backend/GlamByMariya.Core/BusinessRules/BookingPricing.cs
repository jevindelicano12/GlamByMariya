using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

// Pure decimal pricing rules, independent of storage. Persist the inputs as booking snapshots.
public static class BookingPricing
{
    public const int MaximumPeople = 20;
    public static BookingQuote Calculate(CatalogItem service, IEnumerable<CatalogItem> addOns, int people, decimal travelFee, string package = "auto", decimal downPaymentPercent = 50, decimal earlyFee = 0, decimal lateFee = 0)
    {
        if (people < 1 || people > MaximumPeople) throw new InvalidOperationException($"Choose between 1 and {MaximumPeople} people.");
        if (downPaymentPercent is < 1 or > 100 || decimal.Round(downPaymentPercent) != downPaymentPercent) throw new InvalidOperationException("Down payment percentage must be a whole number between 1% and 100%.");
        if (package is not ("auto" or "group")) throw new InvalidOperationException("Choose a valid package.");
        if (service.Price <= 0 || Money.Round(service.Price) != service.Price || travelFee < 0 || Money.Round(travelFee) != travelFee || earlyFee < 0 || Money.Round(earlyFee) != earlyFee || lateFee < 0 || Money.Round(lateFee) != lateFee)
            throw new InvalidOperationException("Service and travel prices must be valid peso amounts with at most two decimal places.");
        if (service.GroupPrice.HasValue && (service.GroupPrice <= 0 || service.GroupPrice > service.Price || Money.Round(service.GroupPrice.Value) != service.GroupPrice || service.GroupMinimumPeople < 2))
            throw new InvalidOperationException("The group rate is not configured correctly. Please contact the artist.");
        var eligible = package == "group" && service.GroupPrice.HasValue && people >= service.GroupMinimumPeople;
        if (package == "group" && !service.GroupPrice.HasValue) throw new InvalidOperationException("This service does not have a package rate configured.");
        var extras = addOns.ToList();
        if (extras.Any(a => a.Price < 0 || Money.Round(a.Price) != a.Price)) throw new InvalidOperationException("An add-on has an invalid price.");
        var unitPrice = eligible ? service.GroupPrice!.Value : service.Price;
        var original = service.Price * people;
        var discount = original - unitPrice * people;
        var extraTotal = extras.Sum(a => a.Price);
        // Travel is discussed separately and is never part of the online booking total.
        var final = original - discount + extraTotal + earlyFee + lateFee;
        var deposit = Money.Percentage(final, downPaymentPercent);
        return new(service.Name, people, package, service.Price, service.GroupPrice, service.GroupMinimumPeople,
            unitPrice, original, discount, extraTotal, 0, final, deposit, final - deposit, downPaymentPercent,
            checked(service.Minutes * people + extras.Sum(a => a.Minutes)), earlyFee, lateFee);
    }
}
