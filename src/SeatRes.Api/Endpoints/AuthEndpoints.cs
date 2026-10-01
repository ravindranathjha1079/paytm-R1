using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SeatRes.Api.Auth;
using SeatRes.Api.Domain;

namespace SeatRes.Api.Endpoints;

public sealed record TokenRequest(string? UserId);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/token", (TokenRequest body, TokenService tokens) =>
        {
            if (!CurrentUser.IsValidUserId(body.UserId))
                return ApiResult.Fail(400, ErrorCodes.BadRequest, "user_id must match ^[A-Za-z0-9_-]{1,64}$");
            var (token, expires) = tokens.Issue(body.UserId!, "user");
            return ApiResult.Ok(new { Token = token, UserId = body.UserId, ExpiresAt = expires });
        });

        app.MapPost("/auth/admin-token", (HttpRequest req, TokenService tokens, IOptions<AuthOptions> auth) =>
        {
            var expected = auth.Value.AdminKey;
            var given = req.Headers["X-Admin-Key"].ToString();
            if (expected.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
                return ApiResult.Fail(401, ErrorCodes.Unauthorized, "invalid admin key");
            var (token, expires) = tokens.Issue("admin", "admin");
            return ApiResult.Ok(new { Token = token, ExpiresAt = expires });
        });

        app.MapGet("/me", (HttpContext ctx) => ApiResult.Ok(new
        {
            UserId = ctx.User.UserId(),
            Role = ctx.User.FindFirst("role")?.Value,
        })).RequireAuthorization(AuthSetup.UserPolicy);
    }
}
