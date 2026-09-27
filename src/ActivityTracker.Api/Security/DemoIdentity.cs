using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ActivityTracker.Api.Security;

// Explicit demo-only authentication. Tenant is derived from a server-owned identity mapping.
// Replace this handler with JWT/OIDC validation and membership authorization before real use.
public sealed class DemoIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IConfiguration config)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!config.GetValue<bool>("Demo:Enabled"))
            return Task.FromResult(AuthenticateResult.Fail("Demo authentication is disabled."));
        string token = Request.Headers.Authorization.ToString();
        (string User, string Tenant)? identity = token switch
        {
            "Bearer demo-alice" => ("alice", "north"),
            "Bearer demo-alex" => ("alex", "north"),
            "Bearer demo-bob" => ("bob", "south"),
            _ => null
        };
        if (identity is null) return Task.FromResult(AuthenticateResult.NoResult());
        Claim[] claims = [new(ClaimTypes.NameIdentifier, identity.Value.User), new("tenant", identity.Value.Tenant)];
        AuthenticationTicket ticket = new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
public static class IdentityExtensions
{
    public static string TenantId(this HttpContext context) => context.User.FindFirstValue("tenant")!;
    public static string UserId(this HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
}
