using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TaRegistry.Web.Data;

namespace TaRegistry.Web.Pages.Admin;

public sealed class LogoutModel(SignInManager<AdminUser> signIn) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync();
        return RedirectToPage("/Admin/Login");
    }
}
