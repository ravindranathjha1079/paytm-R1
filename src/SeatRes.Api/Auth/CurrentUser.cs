using System.Security.Claims;
using System.Text.RegularExpressions;

namespace SeatRes.Api.Auth;

public static partial class CurrentUser
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex UserIdPattern();

    public static bool IsValidUserId(string? userId) => userId is not null && UserIdPattern().IsMatch(userId);

    /// <summary>The only source of identity: the validated token's subject.</summary>
    public static string UserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("sub") ?? throw new InvalidOperationException("authenticated principal has no sub claim");
}
