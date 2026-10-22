using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaRegistry.Web.Data;

namespace TaRegistry.Web.Pages.Admin;

[AllowAnonymous]
public sealed class LoginModel(SignInManager<AdminUser> signIn) : PageModel
{
    public string? Error { get; private set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToPage("/Admin/Index");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string email, string password)
    {
        var result = await signIn.PasswordSignInAsync(email.Trim(), password, isPersistent: true, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            Error = result.IsLockedOut ? "This account is locked for a few minutes." : "The email or password is wrong.";
            return Page();
        }

        return RedirectToPage("/Admin/Index");
    }
}
