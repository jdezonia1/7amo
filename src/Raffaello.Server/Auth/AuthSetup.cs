using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.Extensions.Options;
using Raffaello.Core.Remote;
using Raffaello.Server.Data;

namespace Raffaello.Server.Auth;

public static class AuthSchemes
{
    public const string Smart = "Raffaello";
    public const string Token = "RaffaelloToken";
    public const string RoleClaim = "raffaello:role";
    public const string DisplayClaim = "raffaello:display";
}

/// <summary>Bearer tokens issued by /api/v1/auth/login (app accounts). SignalR sends the token as ?access_token=.</summary>
public sealed class TokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly UserStore _users;

    public TokenAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, UserStore users)
        : base(options, logger, encoder) => _users = users;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken(Request);
        if (token is null) return Task.FromResult(AuthenticateResult.NoResult());
        var u = _users.ValidateToken(token);
        if (u is null) return Task.FromResult(AuthenticateResult.Fail("Invalid or expired token."));
        var id = new ClaimsIdentity(Claims(u), AuthSchemes.Token, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(id), AuthSchemes.Token)));
    }

    public static string? ReadToken(HttpRequest req)
    {
        var h = req.Headers.Authorization.ToString();
        if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return h[7..].Trim();
        if (req.Path.StartsWithSegments(ApiRoutes.ChangesHub) && req.Query.TryGetValue("access_token", out var q) && q.Count > 0) return q[0];
        return null;
    }

    public static IEnumerable<Claim> Claims(UserRow u) => new[]
    {
        new Claim(ClaimTypes.Name, u.UserName), new Claim(ClaimTypes.Role, u.Role),
        new Claim(AuthSchemes.RoleClaim, u.Role), new Claim(AuthSchemes.DisplayClaim, u.DisplayName),
    };
}

/// <summary>
/// Windows (Negotiate / Kerberos / NTLM) users: maps DOMAIN\user to a Users row and adds the role claim. Unknown Windows
/// users are created on first contact with <see cref="ServerOptions.DefaultWindowsRole"/> (SITE = read + statements), so IT
/// does not have to pre-register everyone; an ADMIN raises roles in Settings.
/// </summary>
public sealed class WindowsRoleClaims : IClaimsTransformation
{
    private readonly UserStore _users;
    private readonly ServerOptions _opt;
    public WindowsRoleClaims(UserStore users, ServerOptions opt) { _users = users; _opt = opt; }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } identity || identity.AuthenticationType == AuthSchemes.Token
            || principal.HasClaim(c => c.Type == AuthSchemes.RoleClaim)) return Task.FromResult(principal);
        var account = identity.Name ?? "";
        var u = _users.FindWindows(account);
        if (u is null && _opt.AutoCreateWindowsUsers)
        {
            var shortName = account.Contains('\\') ? account[(account.IndexOf('\\') + 1)..] : account;
            try { u = _users.Create(shortName, _opt.DefaultWindowsRole, null, shortName, account); }
            catch (WriteRejectedException) { u = _users.FindWindows(account); }
        }
        if (u is null || !u.Active) return Task.FromResult(principal);
        var extra = new ClaimsIdentity(new[]
        {
            new Claim(AuthSchemes.RoleClaim, u.Role), new Claim(ClaimTypes.Role, u.Role), new Claim(AuthSchemes.DisplayClaim, u.DisplayName),
            new Claim("raffaello:user", u.UserName),
        });
        principal.AddIdentity(extra);
        return Task.FromResult(principal);
    }
}

public static class AuthSetup
{
    public static void AddRaffaelloAuth(this IServiceCollection services, ServerOptions opt)
    {
        var auth = services.AddAuthentication(AuthSchemes.Smart)
            .AddPolicyScheme(AuthSchemes.Smart, "Token or Windows", o =>
            {
                o.ForwardDefaultSelector = ctx =>
                    TokenAuthHandler.ReadToken(ctx.Request) != null || !opt.WindowsAuth ? AuthSchemes.Token : NegotiateDefaults.AuthenticationScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, TokenAuthHandler>(AuthSchemes.Token, _ => { });
        if (opt.WindowsAuth)
        {
            auth.AddNegotiate();
            services.AddTransient<IClaimsTransformation, WindowsRoleClaims>();
        }
        services.AddAuthorization(o =>
        {
            o.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthSchemes.Smart)
                .RequireAuthenticatedUser().RequireClaim(AuthSchemes.RoleClaim).Build();
        });
    }

    /// <summary>The authenticated caller as a store identity (user / role / machine / client id from headers).</summary>
    public static StoreIdentity Identity(this HttpContext ctx)
    {
        var p = ctx.User;
        var user = p.FindFirst("raffaello:user")?.Value ?? p.Identity?.Name ?? "?";
        var role = p.FindFirst(AuthSchemes.RoleClaim)?.Value ?? "";
        var machine = ctx.Request.Headers[ApiRoutes.MachineHeader].ToString();
        if (string.IsNullOrWhiteSpace(machine)) machine = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        var client = ctx.Request.Headers[ApiRoutes.ClientHeader].ToString();
        return new StoreIdentity(user, role, machine.Length > 64 ? machine[..64] : machine, client, p.Identity?.AuthenticationType ?? "");
    }

    public static string DisplayName(this HttpContext ctx) => ctx.User.FindFirst(AuthSchemes.DisplayClaim)?.Value ?? ctx.Identity().User;
}
