using System.Security.Cryptography;
using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Claude;

/// <param name="Hint">The start of the key, to recognise it (the full key is shown only once).</param>
public record PersonalKeyDto(int Id, string Name, string Hint, DateTime CreatedAt, DateTime? ExpiresAt, DateTime? LastUsedAt,
    DateTime? RevokedAt, bool IsActive);

/// <param name="ExpiresInDays">1–365, or null for a key that never expires (it can still be revoked any time).</param>
public record CreatePersonalKeyRequest(string Name, int? ExpiresInDays);

/// <summary>The new key. <c>Secret</c> is shown once and never stored.</summary>
public record CreatedPersonalKeyDto(PersonalKeyDto Key, string Secret);

public class CreatePersonalKeyRequestValidator : AbstractValidator<CreatePersonalKeyRequest>
{
    public CreatePersonalKeyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ExpiresInDays).InclusiveBetween(1, 365).When(x => x.ExpiresInDays.HasValue);
    }
}

/// <summary>Personal keys that connect Claude (Claude Code / Claude Desktop) to TaskVeta as the current user.</summary>
public interface IPersonalKeyService
{
    Task<IReadOnlyList<PersonalKeyDto>> GetMineAsync(CancellationToken ct = default);
    Task<CreatedPersonalKeyDto> CreateAsync(CreatePersonalKeyRequest request, CancellationToken ct = default);
    Task RevokeAsync(int id, CancellationToken ct = default);
}

public class PersonalKeyService(IApplicationDbContext db, ICurrentUser currentUser, ITokenService tokens, IAuditService audit,
    ICompanyClock clock) : IPersonalKeyService
{
    public const int MaxActiveKeys = 10;

    public async Task<IReadOnlyList<PersonalKeyDto>> GetMineAsync(CancellationToken ct = default)
    {
        var me = RequireUser();
        var now = clock.UtcNow;
        var keys = await db.PersonalAccessTokens.AsNoTracking().Where(k => k.UserId == me)
            .OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return keys.Select(k => ToDto(k, now)).ToList();
    }

    public async Task<CreatedPersonalKeyDto> CreateAsync(CreatePersonalKeyRequest r, CancellationToken ct = default)
    {
        var me = RequireUser();
        if (currentUser.Via is not null) throw new ForbiddenException("Personal keys can only be created in the TaskVeta app.");
        var now = clock.UtcNow;
        var active = await db.PersonalAccessTokens.CountAsync(k => k.UserId == me && k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now), ct);
        if (active >= MaxActiveKeys) throw new BadRequestException($"You already have {MaxActiveKeys} active keys. Revoke one you no longer use first.");

        var secret = PersonalAccessToken.KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var key = new PersonalAccessToken
        {
            UserId = me, Name = r.Name.Trim(), TokenHash = tokens.HashToken(secret), Hint = secret[..8] + "…",
            ExpiresAt = r.ExpiresInDays is int days ? now.AddDays(days) : null,
        };
        db.PersonalAccessTokens.Add(key);
        await db.SaveChangesAsync(ct);
        audit.Log("Create", "PersonalKey", key.Id, newValues: new { key.Name, key.Hint, key.ExpiresAt });
        await db.SaveChangesAsync(ct);
        return new CreatedPersonalKeyDto(ToDto(key, now), secret);
    }

    public async Task RevokeAsync(int id, CancellationToken ct = default)
    {
        var me = RequireUser();
        var key = await db.PersonalAccessTokens.FirstOrDefaultAsync(k => k.Id == id && k.UserId == me, ct)
                  ?? throw new NotFoundException("Personal key", id);
        if (key.RevokedAt is not null) return;
        key.RevokedAt = clock.UtcNow;
        audit.Log("Revoke", "PersonalKey", key.Id, newValues: new { key.Name, key.Hint });
        await db.SaveChangesAsync(ct);
    }

    private int RequireUser() => currentUser.UserId ?? throw new ForbiddenException();

    private static PersonalKeyDto ToDto(PersonalAccessToken k, DateTime now) =>
        new(k.Id, k.Name, k.Hint, k.CreatedAt, k.ExpiresAt, k.LastUsedAt, k.RevokedAt, k.IsActive(now));
}
