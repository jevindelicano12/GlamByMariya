using System.Security.Cryptography;
using System.Text;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace GlamByMariya.Infrastructure.Services;

// Administrator invitations: secure single-use expiring tokens. The role is
// fixed to Admin server-side; invitation flows can never mint a SuperAdmin.
public interface IInvitationService
{
    AdminInvitation Invite(string email, Guid invitedBy);
    (AdminInvitation Invite, string Token) InviteWithToken(string email, Guid invitedBy);
    IReadOnlyList<AdminInvitation> Pending();
    AdminInvitation Accept(string email, string token);
    void Revoke(Guid id, Guid actor);
}

public class InvitationService(TimeProvider clock) : IInvitationService
{
    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("invite:" + token)));

    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    protected List<AdminInvitation> Live(IEnumerable<AdminInvitation> all)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return all.Where(i => i.AcceptedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt).ToList();
    }

    protected static AdminInvitation Build(string email, Guid invitedBy, DateTime now, out string token)
    {
        email = email.Trim();
        if (!MailAddressOk(email)) throw new InvalidOperationException("Enter a valid email address.");
        token = NewToken();
        return new AdminInvitation
        {
            Email = email,
            TokenHash = Hash(token),
            InvitedBy = invitedBy,
            ExpiresAt = now.AddHours(72),
            CreatedAt = now,
        };
    }

    private static bool MailAddressOk(string email) =>
        email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out _);

    protected static AdminInvitation Consume(IEnumerable<AdminInvitation> all, string email, string token, DateTime now)
    {
        // The link's own token picks the invitation. Matching by email alone could pick an
        // older expired invitation for the same address and wrongly reject a fresh link.
        var hash = Encoding.UTF8.GetBytes(Hash((token ?? "").Trim()));
        var match = all.FirstOrDefault(i =>
            i.Email.Equals(email.Trim(), StringComparison.OrdinalIgnoreCase) && i.AcceptedAt == null &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(i.TokenHash), hash));
        if (match == null) throw new InvalidOperationException("This invitation link is invalid or was already used.");
        if (match.ExpiresAt <= now) throw new InvalidOperationException("This invitation expired. Ask the Super Admin for a new one.");
        return match;
    }

    public virtual AdminInvitation Invite(string email, Guid invitedBy) => throw new NotSupportedException();
    public virtual (AdminInvitation Invite, string Token) InviteWithToken(string email, Guid invitedBy) => throw new NotSupportedException();
    public virtual IReadOnlyList<AdminInvitation> Pending() => throw new NotSupportedException();
    public virtual AdminInvitation Accept(string email, string token) => throw new NotSupportedException();
    public virtual void Revoke(Guid id, Guid actor) => throw new NotSupportedException();
}

// Demo / non-public-schema modes: process memory only.
public sealed class MemoryInvitationService(TimeProvider clock) : InvitationService(clock)
{
    private readonly Dictionary<string, AdminInvitation> invites = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public override AdminInvitation Invite(string email, Guid invitedBy)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var invite = Build(email, invitedBy, now, out _);
            invites[invite.TokenHash] = invite;
            return invite;
        }
    }

    // Returns the invite plus its single-use plaintext token (kept server-side
    // only until the controller emails it; never persisted or logged).
    public override (AdminInvitation Invite, string Token) InviteWithToken(string email, Guid invitedBy)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var invite = Build(email, invitedBy, now, out var token);
            invites[invite.TokenHash] = invite;
            return (invite, token);
        }
    }

    public override IReadOnlyList<AdminInvitation> Pending()
    {
        lock (gate) return Live(invites.Values);
    }

    public override AdminInvitation Accept(string email, string token)
    {
        lock (gate)
        {
            var invite = Consume(invites.Values, email, token, clock.GetUtcNow().UtcDateTime);
            invite.AcceptedAt = clock.GetUtcNow().UtcDateTime;
            return invite;
        }
    }

    public override void Revoke(Guid id, Guid actor)
    {
        lock (gate)
        {
            var invite = invites.Values.FirstOrDefault(i => i.Id == id)
                ?? throw new InvalidOperationException("Invitation not found.");
            if (invite.AcceptedAt != null) throw new InvalidOperationException("This invitation was already used.");
            invites.Remove(invite.TokenHash);
        }
    }
}

// Public-schema mode: PostgreSQL table. Tokens hashed; plaintext never stored.
public sealed class PostgresInvitationService(
    PublicMakeupDbContext pdb, IStudioStore store, TimeProvider clock) : InvitationService(clock)
{
    private IQueryable<InvitationRow> Query() => pdb.Set<InvitationRow>().AsNoTracking();

    public override (AdminInvitation Invite, string Token) InviteWithToken(string email, Guid invitedBy)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var invite = Build(email, invitedBy, now, out var token);
        InTransaction(() =>
        {
            pdb.Database.ExecuteSqlInterpolated($"SELECT set_config('makeup.actor_id',{invitedBy.ToString()},true)");
            pdb.Database.ExecuteSqlInterpolated(
                $"INSERT INTO public.admin_invitations(id,email,token_hash,invited_by,expires_at) VALUES({invite.Id},{invite.Email},{invite.TokenHash},{invite.InvitedBy},{invite.ExpiresAt})");
        });
        invite.CreatedAt = now;
        return (invite, token);
    }

    public override AdminInvitation Invite(string email, Guid invitedBy) => InviteWithToken(email, invitedBy).Invite;

    public override IReadOnlyList<AdminInvitation> Pending() =>
        Live(Query().ToList().Select(r => r.ToModel()).ToList());

    public override AdminInvitation Accept(string email, string token)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = Query().ToList();
        var invite = Consume(rows.Select(r => r.ToModel()), email, token, now);
        InTransaction(() =>
        {
            pdb.Database.ExecuteSqlInterpolated($"SELECT set_config('makeup.actor_id',{invite.InvitedBy.ToString()},true)");
            if (pdb.Database.ExecuteSqlInterpolated(
                $"UPDATE public.admin_invitations SET accepted_at={now} WHERE id={invite.Id} AND accepted_at IS NULL") == 0)
                throw new InvalidOperationException("This invitation is invalid or was already used.");
        });
        invite.AcceptedAt = now;
        return invite;
    }

    public override void Revoke(Guid id, Guid actor)
    {
        InTransaction(() =>
        {
            pdb.Database.ExecuteSqlInterpolated($"SELECT set_config('makeup.actor_id',{actor.ToString()},true)");
            var row = Query().FirstOrDefault(r => r.Id == id)
                ?? throw new InvalidOperationException("Invitation not found.");
            if (row.AcceptedAt != null) throw new InvalidOperationException("This invitation was already used.");
            pdb.Database.ExecuteSqlInterpolated($"DELETE FROM public.admin_invitations WHERE id={id}");
        });
    }

    // The app's context retries transient failures, which requires manual transactions to
    // run inside its execution strategy.
    private void InTransaction(Action work) => pdb.Database.CreateExecutionStrategy().Execute(() =>
    {
        using var tx = pdb.Database.BeginTransaction();
        work();
        tx.Commit();
    });
}

public sealed class InvitationRow
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public Guid InvitedBy { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public AdminInvitation ToModel() => new()
    {
        Id = Id, Email = Email, TokenHash = TokenHash, InvitedBy = InvitedBy,
        ExpiresAt = ExpiresAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(ExpiresAt, DateTimeKind.Utc) : ExpiresAt,
        AcceptedAt = AcceptedAt, CreatedAt = CreatedAt,
    };
}
