using CompanyManagement.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace CompanyManagement.Domain.Entities;

public class ApplicationUser : IdentityUser<int>, IAuditableEntity
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string PreferredLanguage { get; set; } = "en";
    public DateTime? LastLoginAt { get; set; }
    /// <summary>
    /// Set when the account was given a temporary password (client accounts). Until the user picks their own password,
    /// the API only allows the auth endpoints.
    /// </summary>
    public bool MustChangePassword { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Employee? Employee { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}

public class ApplicationRole : IdentityRole<int>
{
    public ApplicationRole() { }
    public ApplicationRole(string name, string description) : base(name) => Description = description;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class RefreshToken : BaseEntity
{
    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    /// <summary>SHA-256 hash of the token. The raw token is never persisted.</summary>
    public string TokenHash { get; set; } = string.Empty;
    /// <summary>All tokens produced by rotation from one login share a family; reuse revokes the family.</summary>
    public Guid FamilyId { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? CreatedByIp { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAt is null && ExpiresAt > utcNow;
}

/// <summary>
/// A personal key that lets Claude (Claude Code / Claude Desktop, through the MCP endpoint) act as this user.
/// Only valid on <c>/mcp</c>. The raw key is shown once; only its SHA-256 hash is stored.
/// </summary>
public class PersonalAccessToken : BaseEntity
{
    public const string KeyPrefix = "tvk_";

    public int UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    /// <summary>What the key is for, e.g. "Laptop – Claude Code".</summary>
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    /// <summary>First characters of the key, to recognise it in the list (e.g. "tvk_3fA9").</summary>
    public string Hint { get; set; } = string.Empty;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > utcNow);
}
