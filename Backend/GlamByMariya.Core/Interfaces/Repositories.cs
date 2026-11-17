using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.Interfaces;

// Read contracts shared by the Demo store and the PostgreSQL store.
public interface IServiceRepository { List<CatalogItem> Services { get; } }
public interface IAddOnRepository { List<CatalogItem> AddOns { get; } }
public interface IBookingRepository { List<Booking> Bookings { get; } }
public interface IPaymentRepository { List<Payment> Payments { get; } }
