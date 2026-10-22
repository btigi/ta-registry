using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TaRegistry.Web;
using TaRegistry.Web.Data;
using TaRegistry.Web.Endpoints;
using TaRegistry.Web.Services;
using TaRegistry.Web.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RegistryOptions>(builder.Configuration.GetSection("Registry"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<SigningOptions>(builder.Configuration.GetSection("Signing"));
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("Admin"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<StorageLayout>();
builder.Services.AddSingleton<FileBlobStore>();

builder.Services.AddDbContext<RegistryDb>((services, options) =>
{
    var layout = services.GetRequiredService<StorageLayout>();
    options.UseSqlite("Data Source=" + layout.Database);
});

builder.Services.AddIdentity<AdminUser, IdentityRole>(options =>
    {
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.User.RequireUniqueEmail = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<RegistryDb>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/admin/login";
    options.AccessDeniedPath = "/admin/login";
    options.Cookie.Name = "ta-registry-admin";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.DictionaryKeyPolicy = null;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AllowAnonymousToPage("/Admin/Login");
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 4L * 1024 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;
});

builder.Services.AddScoped<SigningKeyService>();
builder.Services.AddScoped<DescriptorService>();
builder.Services.AddScoped<CataloguePublisher>();
builder.Services.AddScoped<PackageAdminService>();
builder.Services.AddScoped<DownloadService>();
builder.Services.AddScoped<Bootstrapper>();
builder.Services.AddHostedService<BootstrapHostedService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.UseDeveloperExceptionPage();
else
{
    app.UseExceptionHandler(handler => handler.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            await context.Response.WriteAsJsonAsync(new Dictionary<string, string>
            {
                ["error"] = "internal",
                ["message"] = "The registry failed."
            });
            return;
        }

        await context.Response.WriteAsync("The registry failed.");
    }));
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapRegistry();
app.Run();

public partial class Program;
