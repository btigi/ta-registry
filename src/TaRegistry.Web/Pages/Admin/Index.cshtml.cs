using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Web.Data;

namespace TaRegistry.Web.Pages.Admin;

public sealed class DashboardModel(RegistryDb db) : PageModel
{
    public string Name { get; private set; } = "";
    public string RegistryId { get; private set; } = "";
    public int PackageCount { get; private set; }
    public long Sequence { get; private set; }
    public string Expires { get; private set; } = "—";
    public bool Dirty { get; private set; }
    public IReadOnlyList<DownloadEntity> Recent { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.Settings.AsNoTracking().SingleAsync(cancellationToken);
        var state = await db.CatalogueStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        Name = settings.Name;
        RegistryId = settings.RegistryId;
        PackageCount = await db.Packages.CountAsync(cancellationToken);
        Sequence = state?.Sequence ?? 0;
        Expires = state?.Expires ?? "—";
        Dirty = state?.Dirty ?? false;
        Recent = await db.Downloads.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(12).ToListAsync(cancellationToken);
    }
}
