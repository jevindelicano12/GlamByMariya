using System.Collections.Concurrent;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Integrations;

namespace GlamByMariya.Infrastructure.Services;

/// <summary>Sends a best-effort email reminder 24 hours before confirmed appointments.</summary>
public sealed class AppointmentReminderService(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<AppointmentReminderService> log) : BackgroundService
{
    // shortcut: in-memory deduplication per (bookingId, start); a server restart within the 24h window could re-send once.
    private readonly ConcurrentDictionary<Guid, DateTime> _sent = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAndSendAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning("Appointment reminder check failed: {Reason}", e.Message); }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    internal async Task CheckAndSendAsync(CancellationToken ct = default)
    {
        if (config.GetValue("Email:DeliveryDisabled", false) || string.IsNullOrWhiteSpace(config["Email:BrevoApiKey"]))
            return;

        List<(Guid Id, string Reference, string Service, DateTime Start, string Address, string Area, string Email)> due;
        using (var scope = scopes.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IStudioStore>();
            lock (store.Gate)
            {
                var now = store.Now;
                var cutoff = now.AddHours(24);
                var users = store.Users.Where(u => !string.IsNullOrWhiteSpace(u.Email)).ToDictionary(u => u.Id, u => u.Email);
                due = store.Bookings
                    .Where(b => b.Status == "Confirmed" && b.Start > now && b.Start <= cutoff && users.ContainsKey(b.CustomerId))
                    .Where(b => !_sent.TryGetValue(b.Id, out var prev) || prev != b.Start)
                    .Select(b => (b.Id, b.Reference, b.ServiceDescription, b.Start, b.Address, b.Area, users[b.CustomerId]))
                    .ToList();
            }

            if (due.Count == 0) return;
            var brevo = scope.ServiceProvider.GetRequiredService<BrevoEmailSender>();
            foreach (var item in due)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await brevo.SendAsync(item.Email,
                        $"Glam by Mariya {item.Reference}: appointment reminder",
                        $"Reminder: your {item.Service} appointment is coming up on {item.Start:dddd, MMMM d, yyyy h:mm tt}.{Environment.NewLine}Location: {item.Address}, {item.Area}{Environment.NewLine}{Environment.NewLine}Please have clean, moisturized skin and a well-lit space ready. See you soon!");
                    _sent[item.Id] = item.Start;
                }
                catch (Exception e) { log.LogWarning("Reminder email for {Reference} failed: {Reason}", item.Reference, e.Message); }
            }
        }
    }
}
