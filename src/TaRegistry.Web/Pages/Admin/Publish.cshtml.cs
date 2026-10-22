using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Web.Data;
using TaRegistry.Web.Services;

namespace TaRegistry.Web.Pages.Admin;

public sealed class PublishModel(RegistryDb db, CataloguePublisher publisher) : PageModel
{
    public long Sequence { get; private set; }
    public bool Dirty { get; private set; }
    public string? Expires { get; private set; }
    public bool Published { get; private set; }
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        var preview = await publisher.PreviewAsync(cancellationToken);
        Problems = preview.Problems;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var result = await publisher.PublishAsync(cancellationToken);
        await LoadAsync(cancellationToken);
        Published = result.Ok;
        Problems = result.Problems;
        if (result.Ok)
            Sequence = result.Sequence;
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var state = await db.CatalogueStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        Sequence = state?.Sequence ?? 0;
        Dirty = state?.Dirty ?? false;
        Expires = state?.Expires;
    }
}
