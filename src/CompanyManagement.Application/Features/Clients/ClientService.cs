using System.Security.Cryptography;
using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Entities;
using CompanyManagement.Domain.Enums;
using CompanyManagement.Domain.Security;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Features.Clients;

/// <param name="MustChangePassword">The client has not signed in and replaced the temporary password yet.</param>
public record ClientDto(int Id, string Name, string? NameAr, string? ContactName, string Email, string? Phone, string? Notes,
    bool IsActive, bool MustChangePassword, DateTime? LastLoginAt, DateTime CreatedAt, int ProjectCount, int OpenTicketCount);

public record ClientLookupDto(int Id, string Name, string? NameAr);

public record ClientProjectNoteDto(int Id, string Content, bool VisibleToClient, int CreatedByUserId, string CreatedByName,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>A project linked to a client. <c>Id</c> is the link's id; notes are the client-specific changes.</summary>
public record ClientProjectDto(int Id, int ProjectId, string ProjectName, string? ProjectNameAr, string ProjectCode, ProjectStatus ProjectStatus,
    string? LogoVersion, DateTime LinkedAt, IReadOnlyList<ClientProjectNoteDto> Notes);

public record ClientDetailsDto(ClientDto Client, IReadOnlyList<ClientProjectDto> Projects);

public record SaveClientRequest(string Name, string? NameAr, string? ContactName, string Email, string? Phone, string? Notes);

/// <summary>The sign-in details to send to the client. The temporary password is shown only once and must be changed at first sign-in.</summary>
public record ClientCredentialsDto(int ClientId, string Email, string TemporaryPassword);

public record SetClientActiveRequest(bool IsActive);

public record LinkClientProjectRequest(int ProjectId);

public record SaveClientProjectNoteRequest(string Content, bool VisibleToClient);

public class SaveClientRequestValidator : AbstractValidator<SaveClientRequest>
{
    public SaveClientRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(150);
        RuleFor(x => x.NameAr).MaximumLength(150);
        RuleFor(x => x.ContactName).MaximumLength(150);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public class LinkClientProjectRequestValidator : AbstractValidator<LinkClientProjectRequest>
{
    public LinkClientProjectRequestValidator() => RuleFor(x => x.ProjectId).GreaterThan(0);
}

public class SaveClientProjectNoteRequestValidator : AbstractValidator<SaveClientProjectNoteRequest>
{
    public SaveClientProjectNoteRequestValidator() => RuleFor(x => x.Content).NotEmpty().MaximumLength(4000);
}

public interface IClientService
{
    Task<IReadOnlyList<ClientDto>> GetAllAsync(string? search, CancellationToken ct = default);
    Task<IReadOnlyList<ClientLookupDto>> LookupAsync(CancellationToken ct = default);
    Task<ClientDetailsDto> GetAsync(int id, CancellationToken ct = default);
    /// <summary>Creates the client and its account with a temporary password (returned once).</summary>
    Task<ClientCredentialsDto> CreateAsync(SaveClientRequest request, CancellationToken ct = default);
    Task<ClientDetailsDto> UpdateAsync(int id, SaveClientRequest request, CancellationToken ct = default);
    /// <summary>Gives the account a new temporary password (signs the client out everywhere; they must change it again).</summary>
    Task<ClientCredentialsDto> ResetPasswordAsync(int id, CancellationToken ct = default);
    Task<ClientDetailsDto> SetActiveAsync(int id, bool isActive, CancellationToken ct = default);
    /// <summary>Deletes a client without tickets, with its account. Clients with tickets are deactivated instead.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<ClientDetailsDto> LinkProjectAsync(int id, LinkClientProjectRequest request, CancellationToken ct = default);
    Task<ClientDetailsDto> UnlinkProjectAsync(int id, int clientProjectId, CancellationToken ct = default);
    Task<ClientDetailsDto> AddNoteAsync(int id, int clientProjectId, SaveClientProjectNoteRequest request, CancellationToken ct = default);
    Task<ClientDetailsDto> UpdateNoteAsync(int id, int clientProjectId, int noteId, SaveClientProjectNoteRequest request, CancellationToken ct = default);
    Task<ClientDetailsDto> DeleteNoteAsync(int id, int clientProjectId, int noteId, CancellationToken ct = default);
}

/// <summary>Client accounts and the projects delivered to them (people with clients.manage).</summary>
public class ClientService(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IAuditService audit,
    ICurrentUser currentUser,
    ICompanyClock clock) : IClientService
{
    public async Task<IReadOnlyList<ClientDto>> GetAllAsync(string? search, CancellationToken ct = default)
    {
        var query = db.Clients.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(s) || (c.NameAr != null && c.NameAr.Contains(s))
                                     || (c.ContactName != null && c.ContactName.ToLower().Contains(s)) || c.User.Email!.ToLower().Contains(s));
        }
        return await Project(query.OrderBy(c => c.Name)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ClientLookupDto>> LookupAsync(CancellationToken ct = default) =>
        await db.Clients.AsNoTracking().OrderBy(c => c.Name).Select(c => new ClientLookupDto(c.Id, c.Name, c.NameAr)).ToListAsync(ct);

    public async Task<ClientDetailsDto> GetAsync(int id, CancellationToken ct = default)
    {
        var client = await Project(db.Clients.AsNoTracking().Where(c => c.Id == id)).FirstOrDefaultAsync(ct)
                     ?? throw new NotFoundException("Client", id);
        return new ClientDetailsDto(client, await ProjectsAsync(id, includeInternalNotes: true, ct));
    }

    public async Task<ClientCredentialsDto> CreateAsync(SaveClientRequest r, CancellationToken ct = default)
    {
        var email = r.Email.Trim();
        if (await userManager.FindByEmailAsync(email) is not null) throw new ConflictException("An account with this email already exists.");
        var password = TemporaryPassword.Generate();

        return await db.ExecuteInTransactionAsync(async () =>
        {
            var user = new ApplicationUser
            {
                UserName = email, Email = email, FullName = (r.ContactName ?? r.Name).Trim(), EmailConfirmed = true, IsActive = true,
                LockoutEnabled = true, MustChangePassword = true,
            };
            var created = await userManager.CreateAsync(user, password);
            if (!created.Succeeded) throw new BadRequestException(string.Join(" ", created.Errors.Select(e => e.Description)));
            var role = await userManager.AddToRoleAsync(user, Roles.Client);
            if (!role.Succeeded) throw new BadRequestException(string.Join(" ", role.Errors.Select(e => e.Description)));

            var client = new Client { UserId = user.Id, CreatedByUserId = currentUser.UserId };
            Apply(client, r);
            db.Clients.Add(client);
            await db.SaveChangesAsync(ct);
            audit.Log("Create", nameof(Client), client.Id, newValues: new { client.Name, Email = email });
            await db.SaveChangesAsync(ct);
            return new ClientCredentialsDto(client.Id, email, password);
        }, ct);
    }

    public async Task<ClientDetailsDto> UpdateAsync(int id, SaveClientRequest r, CancellationToken ct = default)
    {
        var client = await db.Clients.Include(c => c.User).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Client", id);
        var email = r.Email.Trim();
        var old = new { client.Name, client.NameAr, client.ContactName, client.Phone, client.User.Email };
        if (!string.Equals(client.User.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            if (await userManager.FindByEmailAsync(email) is { } other && other.Id != client.UserId)
                throw new ConflictException("An account with this email already exists.");
            client.User.Email = email;
            client.User.UserName = email;
            client.User.NormalizedEmail = userManager.NormalizeEmail(email);
            client.User.NormalizedUserName = userManager.NormalizeName(email);
        }
        Apply(client, r);
        client.User.FullName = (r.ContactName ?? r.Name).Trim();
        audit.Log("Update", nameof(Client), id, old, new { client.Name, client.NameAr, client.ContactName, client.Phone, Email = email });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ClientCredentialsDto> ResetPasswordAsync(int id, CancellationToken ct = default)
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Client", id);
        var user = await userManager.FindByIdAsync(client.UserId.ToString()) ?? throw new NotFoundException("User", client.UserId);
        var password = TemporaryPassword.Generate();
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var res = await userManager.ResetPasswordAsync(user, token, password);
        if (!res.Succeeded) throw new BadRequestException(string.Join(" ", res.Errors.Select(e => e.Description)));
        user.MustChangePassword = true;
        await userManager.UpdateAsync(user);
        await userManager.SetLockoutEndDateAsync(user, null);
        await userManager.ResetAccessFailedCountAsync(user);
        await RevokeSessionsAsync(user.Id, "Temporary password issued", ct);
        audit.Log("ResetPassword", nameof(Client), id);
        await db.SaveChangesAsync(ct);
        return new ClientCredentialsDto(id, user.Email!, password);
    }

    public async Task<ClientDetailsDto> SetActiveAsync(int id, bool isActive, CancellationToken ct = default)
    {
        var client = await db.Clients.Include(c => c.User).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Client", id);
        if (client.User.IsActive != isActive)
        {
            client.User.IsActive = isActive;
            if (!isActive) await RevokeSessionsAsync(client.UserId, "Client deactivated", ct);
            audit.Log(isActive ? "Activate" : "Deactivate", nameof(Client), id);
            await db.SaveChangesAsync(ct);
        }
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Client", id);
        if (await db.SupportTickets.AnyAsync(t => t.ClientId == id, ct))
            throw new BadRequestException("This client has support tickets. Deactivate the account instead of deleting it.");
        var user = await userManager.FindByIdAsync(client.UserId.ToString());
        await db.ExecuteInTransactionAsync(async () =>
        {
            db.Clients.Remove(client);
            db.RefreshTokens.RemoveRange(db.RefreshTokens.Where(t => t.UserId == client.UserId));
            audit.Log("Delete", nameof(Client), id, new { client.Name, user?.Email });
            await db.SaveChangesAsync(ct);
            if (user is not null)
            {
                var res = await userManager.DeleteAsync(user);
                if (!res.Succeeded) throw new BadRequestException(string.Join(" ", res.Errors.Select(e => e.Description)));
            }
            return true;
        }, ct);
    }

    public async Task<ClientDetailsDto> LinkProjectAsync(int id, LinkClientProjectRequest r, CancellationToken ct = default)
    {
        if (!await db.Clients.AnyAsync(c => c.Id == id, ct)) throw new NotFoundException("Client", id);
        if (!await db.Projects.AnyAsync(p => p.Id == r.ProjectId, ct)) throw new BadRequestException("The project does not exist.");
        if (!await db.ClientProjects.AnyAsync(cp => cp.ClientId == id && cp.ProjectId == r.ProjectId, ct))
        {
            db.ClientProjects.Add(new ClientProject { ClientId = id, ProjectId = r.ProjectId });
            audit.Log("LinkProject", nameof(Client), id, newValues: new { r.ProjectId });
            await db.SaveChangesAsync(ct);
        }
        return await GetAsync(id, ct);
    }

    public async Task<ClientDetailsDto> UnlinkProjectAsync(int id, int clientProjectId, CancellationToken ct = default)
    {
        var link = await db.ClientProjects.FirstOrDefaultAsync(cp => cp.Id == clientProjectId && cp.ClientId == id, ct)
                   ?? throw new NotFoundException("Client project", clientProjectId);
        db.ClientProjects.Remove(link);
        audit.Log("UnlinkProject", nameof(Client), id, new { link.ProjectId });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ClientDetailsDto> AddNoteAsync(int id, int clientProjectId, SaveClientProjectNoteRequest r, CancellationToken ct = default)
    {
        if (!await db.ClientProjects.AnyAsync(cp => cp.Id == clientProjectId && cp.ClientId == id, ct))
            throw new NotFoundException("Client project", clientProjectId);
        db.ClientProjectNotes.Add(new ClientProjectNote
        {
            ClientProjectId = clientProjectId, Content = r.Content.Trim(), VisibleToClient = r.VisibleToClient, CreatedByUserId = currentUser.UserId!.Value,
        });
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ClientDetailsDto> UpdateNoteAsync(int id, int clientProjectId, int noteId, SaveClientProjectNoteRequest r, CancellationToken ct = default)
    {
        var note = await FindNoteAsync(id, clientProjectId, noteId, ct);
        note.Content = r.Content.Trim();
        note.VisibleToClient = r.VisibleToClient;
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<ClientDetailsDto> DeleteNoteAsync(int id, int clientProjectId, int noteId, CancellationToken ct = default)
    {
        var note = await FindNoteAsync(id, clientProjectId, noteId, ct);
        db.ClientProjectNotes.Remove(note);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>The client's projects with their notes (only the notes shared with the client unless <paramref name="includeInternalNotes"/>).</summary>
    public static async Task<IReadOnlyList<ClientProjectDto>> ProjectsOfAsync(IApplicationDbContext db, int clientId, bool includeInternalNotes,
        CancellationToken ct)
    {
        var links = await db.ClientProjects.AsNoTracking().Where(cp => cp.ClientId == clientId).OrderBy(cp => cp.Project.Name)
            .Select(cp => new { cp.Id, cp.ProjectId, cp.Project.Name, cp.Project.NameAr, cp.Project.Code, cp.Project.Status, cp.Project.LogoStoredFileName, cp.CreatedAt })
            .ToListAsync(ct);
        var ids = links.Select(l => l.Id).ToArray();
        var notes = await db.ClientProjectNotes.AsNoTracking()
            .Where(n => ids.Contains(n.ClientProjectId) && (includeInternalNotes || n.VisibleToClient))
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new { n.ClientProjectId, Dto = new ClientProjectNoteDto(n.Id, n.Content, n.VisibleToClient, n.CreatedByUserId, n.CreatedBy.FullName,
                n.CreatedAt, n.UpdatedAt) })
            .ToListAsync(ct);
        return links.Select(l => new ClientProjectDto(l.Id, l.ProjectId, l.Name, l.NameAr, l.Code, l.Status,
            l.LogoStoredFileName is { } f ? Path.GetFileNameWithoutExtension(f) : null, l.CreatedAt,
            notes.Where(n => n.ClientProjectId == l.Id).Select(n => n.Dto).ToList())).ToList();
    }

    private Task<IReadOnlyList<ClientProjectDto>> ProjectsAsync(int clientId, bool includeInternalNotes, CancellationToken ct) =>
        ProjectsOfAsync(db, clientId, includeInternalNotes, ct);

    private async Task<ClientProjectNote> FindNoteAsync(int id, int clientProjectId, int noteId, CancellationToken ct) =>
        await db.ClientProjectNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.ClientProjectId == clientProjectId
                                                             && n.ClientProject.ClientId == id, ct)
        ?? throw new NotFoundException("Note", noteId);

    private static IQueryable<ClientDto> Project(IQueryable<Client> query) => query.Select(c => new ClientDto(
        c.Id, c.Name, c.NameAr, c.ContactName, c.User.Email!, c.Phone, c.Notes, c.User.IsActive, c.User.MustChangePassword, c.User.LastLoginAt,
        c.CreatedAt, c.Projects.Count(),
        c.Tickets.Count(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed)));

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void Apply(Client c, SaveClientRequest r)
    {
        c.Name = r.Name.Trim();
        c.NameAr = Clean(r.NameAr);
        c.ContactName = Clean(r.ContactName);
        c.Phone = Clean(r.Phone);
        c.Notes = Clean(r.Notes);
    }

    private async Task RevokeSessionsAsync(int userId, string reason, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active) { t.RevokedAt = clock.UtcNow; t.RevokedReason = reason; }
    }
}

/// <summary>Readable temporary passwords that satisfy the password rules (upper, lower, digit; 12 characters).</summary>
public static class TemporaryPassword
{
    // No look-alike characters (0/O, 1/l/I) so it can be read over the phone.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";

    public static string Generate()
    {
        var all = Upper + Lower + Digits;
        var chars = new List<char> { Pick(Upper), Pick(Lower), Pick(Digits) };
        while (chars.Count < 12) chars.Add(Pick(all));
        // Shuffle so the required characters are not always first.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        var s = new string(chars.ToArray());
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}
