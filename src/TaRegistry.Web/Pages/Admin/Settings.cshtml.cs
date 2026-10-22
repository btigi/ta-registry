using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Core;
using TaRegistry.Core.Catalogue;
using TaRegistry.Web.Data;
using TaRegistry.Web.Services;

namespace TaRegistry.Web.Pages.Admin;

public sealed class SettingsModel(RegistryDb db, DescriptorService descriptors) : PageModel
{
    public bool Saved { get; private set; }
    public IReadOnlyList<string> Problems { get; private set; } = [];

    [BindProperty] public string RegistryId { get; set; } = "";
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Title { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public string? Author { get; set; }
    [BindProperty] public string? Homepage { get; set; }
    [BindProperty] public string BaseUrl { get; set; } = "";
    [BindProperty] public bool InstallIdRequired { get; set; } = true;
    [BindProperty] public int CatalogueExpiryDays { get; set; } = 30;
    [BindProperty] public int DownloadUrlTtlSeconds { get; set; } = 600;
    [BindProperty] public int KeysPerHour { get; set; } = 30;
    [BindProperty] public int ChallengeAfter { get; set; } = 10;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.Settings.AsNoTracking().SingleAsync(cancellationToken);
        CopyFrom(settings);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        if (CatalogueExpiryDays is < 1 or > 3650)
            problems.Add("Catalogue lifetime must be from 1 to 3650 days.");
        if (DownloadUrlTtlSeconds is < 60 or > 86400)
            problems.Add("Download URL lifetime must be from 60 to 86400 seconds.");
        if (KeysPerHour is < 1 or > 100000)
            problems.Add("Keys per hour must be at least 1.");
        if (ChallengeAfter < 0)
            problems.Add("The challenge threshold cannot be negative.");
        if (!TextRules.IsOrigin(BaseUrl.Trim().TrimEnd('/')))
            problems.Add("Base URL must be an http or https origin with no path or query.");

        var settings = await db.Settings.SingleAsync(cancellationToken);
        var keys = await db.SigningKeys.AsNoTracking().Where(k => k.IncludeInCatalogue).OrderBy(k => k.CreatedAt).ToListAsync(cancellationToken);
        var draft = new RegistrySettings
        {
            RegistryId = RegistryId.Trim(),
            Name = Name.Trim(),
            Title = Blank(Title),
            Description = Blank(Description),
            Author = Blank(Author),
            Homepage = Blank(Homepage),
            BaseUrl = BaseUrl.Trim().TrimEnd('/'),
            InstallIdRequired = InstallIdRequired,
            CatalogueExpiryDays = CatalogueExpiryDays,
            DownloadUrlTtlSeconds = DownloadUrlTtlSeconds,
            KeysPerHour = KeysPerHour,
            ChallengeAfter = ChallengeAfter
        };
        problems.AddRange(CatalogueRules.DescriptorProblems(DescriptorService.Model(draft, keys)));
        if (problems.Count > 0)
        {
            Problems = problems;
            return Page();
        }

        settings.RegistryId = draft.RegistryId;
        settings.Name = draft.Name;
        settings.Title = draft.Title;
        settings.Description = draft.Description;
        settings.Author = draft.Author;
        settings.Homepage = draft.Homepage;
        settings.BaseUrl = draft.BaseUrl;
        settings.InstallIdRequired = draft.InstallIdRequired;
        settings.CatalogueExpiryDays = draft.CatalogueExpiryDays;
        settings.DownloadUrlTtlSeconds = draft.DownloadUrlTtlSeconds;
        settings.KeysPerHour = draft.KeysPerHour;
        settings.ChallengeAfter = draft.ChallengeAfter;
        var state = await db.CatalogueStates.SingleOrDefaultAsync(cancellationToken);
        if (state is not null)
            state.Dirty = true;

        await db.SaveChangesAsync(cancellationToken);
        await descriptors.WriteFileAsync(cancellationToken);
        Saved = true;
        CopyFrom(settings);
        return Page();
    }

    private void CopyFrom(RegistrySettings settings)
    {
        RegistryId = settings.RegistryId;
        Name = settings.Name;
        Title = settings.Title;
        Description = settings.Description;
        Author = settings.Author;
        Homepage = settings.Homepage;
        BaseUrl = settings.BaseUrl;
        InstallIdRequired = settings.InstallIdRequired;
        CatalogueExpiryDays = settings.CatalogueExpiryDays;
        DownloadUrlTtlSeconds = settings.DownloadUrlTtlSeconds;
        KeysPerHour = settings.KeysPerHour;
        ChallengeAfter = settings.ChallengeAfter;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
