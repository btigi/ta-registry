using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaRegistry.Web.Services;

namespace TaRegistry.Web.Pages.Admin.Packages;

[RequestSizeLimit(4L * 1024 * 1024 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = 4L * 1024 * 1024 * 1024)]
public sealed class UploadModel(PackageAdminService packages) : PageModel
{
    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync(IFormFile? package, IFormFile? badge, CancellationToken cancellationToken)
    {
        if (package is null || package.Length == 0)
        {
            Error = "Choose a package file.";
            return Page();
        }

        await using var packageStream = package.OpenReadStream();
        Stream? badgeStream = null;
        if (badge is { Length: > 0 })
            badgeStream = badge.OpenReadStream();

        try
        {
            var result = await packages.UploadAsync(packageStream, package.FileName, badgeStream, cancellationToken);
            if (!result.Ok || result.PackageId is null)
            {
                Error = result.Message;
                return Page();
            }

            TempData["Warnings"] = result.Warnings.Count == 0 ? null : string.Join("\n", result.Warnings);
            TempData["Message"] = result.Message;
            return RedirectToPage("/Admin/Packages/Edit", new { id = result.PackageId });
        }
        finally
        {
            if (badgeStream is not null)
                await badgeStream.DisposeAsync();
        }
    }
}
