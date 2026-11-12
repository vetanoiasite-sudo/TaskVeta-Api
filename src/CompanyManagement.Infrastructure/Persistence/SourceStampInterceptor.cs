using CompanyManagement.Application.Common;
using CompanyManagement.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CompanyManagement.Infrastructure.Persistence;

/// <summary>
/// Marks new history rows, audit entries, comments and documents with where they came from ("Claude" for the MCP endpoint),
/// so the app can show "via Claude" without every service having to remember it.
/// </summary>
public class SourceStampInterceptor(ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null || currentUser.Via is not { } via) return;
        foreach (var entry in context.ChangeTracker.Entries<ISourceTracked>())
            if (entry.State == EntityState.Added && entry.Entity.Source is null)
                entry.Entity.Source = via;
    }
}
