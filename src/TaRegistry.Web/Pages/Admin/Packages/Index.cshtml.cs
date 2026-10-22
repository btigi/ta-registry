using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Web.Data;

namespace TaRegistry.Web.Pages.Admin.Packages;

public sealed class IndexModel(RegistryDb db) : PageModel
{
    public IReadOnlyList<PackageEntity> Packages { get; private set; } = [];

    public async Task OnGetAsync(string? kind, CancellationToken cancellationToken)
    {
        var query = db.Packages.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(p => p.Kind == kind);

        Packages = await query.OrderBy(p => p.Kind).ThenBy(p => p.PackageId).ToListAsync(cancellationToken);
    }
}
