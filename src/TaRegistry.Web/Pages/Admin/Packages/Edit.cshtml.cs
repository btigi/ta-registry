using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Web.Data;
using TaRegistry.Web.Services;

namespace TaRegistry.Web.Pages.Admin.Packages;

public sealed class EditModel(RegistryDb db, PackageAdminService packages) : PageModel
{
    public string Kind { get; private set; } = "";
    public string PackageId { get; private set; } = "";
    public int Release { get; private set; }
    public string Sha256 { get; private set; } = "";
    public IReadOnlyList<string> Problems { get; private set; } = [];

    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string? Version { get; set; }
    [BindProperty] public int? Revision { get; set; }
    [BindProperty] public string? Summary { get; set; }
    [BindProperty] public string? Author { get; set; }
    [BindProperty] public string? Publisher { get; set; }
    [BindProperty] public string? Homepage { get; set; }
    [BindProperty] public string? Notes { get; set; }
    [BindProperty] public string? PublishedDay { get; set; }
    [BindProperty] public string? Tags { get; set; }
    [BindProperty] public string? DetailsJson { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await db.Packages.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        Fill(entity);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await db.Packages.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        try
        {
            var problems = await packages.UpdateAsync(entity, new PackageEdit
            {
                Name = Name,
                Version = Version,
                Revision = Revision,
                Summary = Summary,
                Author = Author,
                Publisher = Publisher,
                Homepage = Homepage,
                Notes = Notes,
                PublishedDay = PublishedDay,
                Tags = Tags,
                DetailsJson = DetailsJson
            }, cancellationToken);

            if (problems.Count > 0)
            {
                Problems = problems;
                FillIdentity(entity);
                return Page();
            }
        }
        catch (JsonException)
        {
            Problems = ["Extra details are not a JSON object."];
            FillIdentity(entity);
            return Page();
        }
        catch (InvalidOperationException ex)
        {
            Problems = [ex.Message];
            FillIdentity(entity);
            return Page();
        }

        TempData["Message"] = "Saved. Publish the catalogue to ship it.";
        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        var entity = await db.Packages.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (entity is null)
            return NotFound();

        await packages.DeleteAsync(entity, cancellationToken);
        return RedirectToPage("/Admin/Packages/Index");
    }

    private void Fill(PackageEntity entity)
    {
        FillIdentity(entity);
        Name = entity.Name;
        Version = entity.Version;
        Revision = entity.Revision;
        Summary = entity.Summary;
        Author = entity.Author;
        Publisher = entity.Publisher;
        Homepage = entity.Homepage;
        Notes = entity.Notes;
        PublishedDay = entity.PublishedDay;
        Tags = TagsText(entity.TagsJson);
        DetailsJson = Pretty(entity.DetailsJson);
    }

    private void FillIdentity(PackageEntity entity)
    {
        Kind = entity.Kind;
        PackageId = entity.PackageId;
        Release = entity.Release;
        Sha256 = entity.Sha256;
    }

    private static string? TagsText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var tags = JsonSerializer.Deserialize<List<string>>(json);
        return tags is null ? null : string.Join(", ", tags);
    }

    private static string? Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }
}
