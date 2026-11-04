using CompanyManagement.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace CompanyManagement.Domain.Entities;

public class ApplicationUser : IdentityUser<int>, IAuditableEntity
{
    public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string PreferredLanguage { get; set; } = "en";
    public DateTime? LastLoginAt { get; set; }
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
