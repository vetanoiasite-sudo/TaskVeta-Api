namespace CompanyManagement.Domain.Common;

public abstract class BaseEntity : IAuditableEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Marker for entities whose CreatedAt/UpdatedAt are maintained automatically.</summary>
public interface IAuditableEntity
{
    DateTime CreatedAt { get; set; }
    DateTime UpdatedAt { get; set; }
}

public class DomainException : Exception
{
    public string Code { get; }
    public DomainException(string message, string code = "domain_error") : base(message) => Code = code;
}

/// <summary>Records where a row came from: null for the TaskVeta app, "Claude" for the MCP endpoint. Stamped on insert.</summary>
public interface ISourceTracked
{
    string? Source { get; set; }
}
