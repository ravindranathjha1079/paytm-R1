using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Auth;

public static class AuthSetup
{
    public const string AdminPolicy = "admin";
    public const string UserPolicy = "user";

    public static IServiceCollection AddSeatResAuth(this IServiceCollection services)
    {
        services.AddSingleton<TokenService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthOptions>>((o, auth) =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = TokenService.Issuer,
                    ValidAudience = TokenService.Audience,
                    IssuerSigningKey = TokenService.SigningKey(auth.Value.JwtKey),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                o.Events = new JwtBearerEvents
                {
                    OnChallenge = async ctx =>
                    {
                        ctx.HandleResponse();
                        await ApiResult.Fail(401, ErrorCodes.Unauthorized, "missing or invalid bearer token").WriteAsync(ctx.HttpContext);
                    },
                    OnForbidden = ctx =>
                        ApiResult.Fail(403, ErrorCodes.Forbidden, "this action requires the admin role").WriteAsync(ctx.HttpContext),
                };
            });

        services.AddOptions<AuthOptions>()
            .Validate(o => o.JwtKey.Length >= 32, "Auth:JwtKey must be at least 32 characters")
            .ValidateOnStart();

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole("admin"))
            .AddPolicy(UserPolicy, p => p.RequireAuthenticatedUser());
        return services;
    }
}
