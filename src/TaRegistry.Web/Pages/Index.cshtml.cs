using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core.Signing;
using TaRegistry.Web.Data;

namespace TaRegistry.Web.Pages;

public sealed class IndexModel(RegistryDb db) : PageModel
{
    public string Name { get; private set; } = "";
    public string? Title { get; private set; }
    public string KeyId { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public long Sequence { get; private set; }
    public bool Dirty { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.Settings.AsNoTracking().SingleAsync(cancellationToken);
        var signer = await db.SigningKeys.AsNoTracking().SingleAsync(k => k.IsSigner, cancellationToken);
        var state = await db.CatalogueStates.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        Name = settings.Name;
        Title = settings.Title;
        KeyId = signer.KeyId;
        Fingerprint = Ed25519Key.TryParsePublic(signer.PublicKey, out var bytes) ? KeyFingerprint.Format(bytes) : signer.PublicKey;
        Sequence = state?.Sequence ?? 0;
        Dirty = state?.Dirty ?? false;
    }
}
