using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core.Signing;
using TaRegistry.Web.Data;
using TaRegistry.Web.Services;

namespace TaRegistry.Web.Pages.Admin;

public sealed class KeysModel(RegistryDb db, SigningKeyService keys) : PageModel
{
    public IReadOnlyList<KeyRow> Keys { get; private set; } = [];
    public string? Message { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostRotateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var id = await keys.RotateAsync(cancellationToken);
            Message = $"Added {id}. Publish so the current key signs a catalogue that includes it.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            Error = ex.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostPromoteAsync(string keyId, CancellationToken cancellationToken)
    {
        try
        {
            await keys.PromoteAsync(keyId, cancellationToken);
            Message = $"{keyId} will sign the next catalogue. Publish only after players trust it.";
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }

        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await db.SigningKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync(cancellationToken);
        Keys = rows.Select(k => new KeyRow(
            k.KeyId,
            k.IsSigner,
            Ed25519Key.TryParsePublic(k.PublicKey, out var bytes) ? KeyFingerprint.Format(bytes) : k.PublicKey)).ToList();
    }

    public sealed record KeyRow(string KeyId, bool IsSigner, string Fingerprint);
}
