using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core;
using TaRegistry.Web.Data;
using TaRegistry.Web.Services;

namespace TaRegistry.Web.Pages;

public sealed class VerifyModel(RegistryDb db, DownloadService downloads, TimeProvider clock) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Code { get; set; }

    public bool Missing { get; private set; }
    public bool Unknown { get; private set; }
    public bool Expired { get; private set; }
    public bool Solved { get; private set; }
    public string? Expires { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(string? code, CancellationToken cancellationToken)
    {
        Code = code;
        if (string.IsNullOrWhiteSpace(Code))
        {
            Missing = true;
            return Page();
        }

        await downloads.SolveChallengeAsync(Code, cancellationToken);
        await LoadAsync(cancellationToken);
        return Page();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            Missing = true;
            return;
        }

        var challenge = await db.Challenges.SingleOrDefaultAsync(c => c.Code == Code, cancellationToken);
        if (challenge is null)
        {
            Unknown = true;
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (challenge.State == "pending" && challenge.ExpiresAt <= now)
            challenge.State = "expired";

        Expires = UtcTimes.Format(challenge.ExpiresAt);
        Solved = challenge.State == "solved";
        Expired = challenge.State == "expired";
    }
}
