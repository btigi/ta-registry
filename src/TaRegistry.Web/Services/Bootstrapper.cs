using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TaRegistry.Core;
using TaRegistry.Core.Catalogue;
using TaRegistry.Core.Signing;
using TaRegistry.Web.Data;

namespace TaRegistry.Web.Services;

public sealed class Bootstrapper(
    RegistryDb db,
    UserManager<AdminUser> users,
    IOptions<RegistryOptions> registryOptions,
    IOptions<AdminOptions> adminOptions,
    IOptions<SigningOptions> signingOptions,
    SigningKeyService keys,
    CataloguePublisher publisher,
    DescriptorService descriptors,
    ILogger<Bootstrapper> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSettingsAsync(cancellationToken);
        await EnsureAdminAsync();
        SealedKeyFile.RequirePassphrase(signingOptions.Value.Passphrase);
        Ed25519Key.RequireKeyId(signingOptions.Value.KeyId);
        await keys.EnsureAsync(cancellationToken);

        var state = await db.CatalogueStates.SingleOrDefaultAsync(cancellationToken);
        if (state?.Json is null)
        {
            var published = await publisher.PublishAsync(cancellationToken);
            if (!published.Ok)
                throw new InvalidOperationException("Could not publish the initial catalogue: " + string.Join(" ", published.Problems));
        }

        await descriptors.WriteFileAsync(cancellationToken);
        logger.LogInformation("Registry ready at {BaseUrl}", (await db.Settings.SingleAsync(cancellationToken)).BaseUrl);
    }

    private async Task EnsureSettingsAsync(CancellationToken cancellationToken)
    {
        if (await db.Settings.AnyAsync(cancellationToken))
            return;

        var options = registryOptions.Value;
        var settings = new RegistrySettings
        {
            Id = 1,
            RegistryId = options.Id,
            Name = options.Name,
            Title = Blank(options.Title),
            Description = Blank(options.Description),
            Author = Blank(options.Author),
            Homepage = Blank(options.Homepage),
            BaseUrl = RequireOrigin(options.BaseUrl),
            InstallIdRequired = !string.Equals(options.InstallId, "none", StringComparison.OrdinalIgnoreCase),
            CatalogueExpiryDays = options.CatalogueExpiryDays,
            DownloadUrlTtlSeconds = options.DownloadUrlTtlSeconds,
            KeysPerHour = options.RateLimit.KeysPerHour,
            ChallengeAfter = options.RateLimit.ChallengeAfter
        };

        var keys = new List<SigningKeyEntity>();
        var problems = CatalogueRules.DescriptorProblems(DescriptorService.Model(settings, keys));
        if (problems.Count > 0)
            throw new InvalidOperationException("Registry configuration is not a valid descriptor: " + string.Join(" ", problems));

        db.Settings.Add(settings);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureAdminAsync()
    {
        if (await users.Users.AnyAsync())
            return;

        var admin = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(admin.Email) || string.IsNullOrWhiteSpace(admin.Password))
            throw new InvalidOperationException("Set Admin:Email and Admin:Password before the first start.");

        var user = new AdminUser
        {
            UserName = admin.Email.Trim(),
            Email = admin.Email.Trim(),
            EmailConfirmed = true
        };
        var created = await users.CreateAsync(user, admin.Password);
        if (!created.Succeeded)
            throw new InvalidOperationException("Could not create the admin user: " + string.Join(" ", created.Errors.Select(e => e.Description)));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string RequireOrigin(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!TextRules.IsOrigin(trimmed))
            throw new InvalidOperationException("Registry:BaseUrl must be an http or https origin with no path or query.");

        return trimmed;
    }
}

public sealed class BootstrapHostedService(IServiceScopeFactory scopes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Bootstrapper>().RunAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
