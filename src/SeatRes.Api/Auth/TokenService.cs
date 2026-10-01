using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SeatRes.Api.Auth;

/// <summary>
/// Stand-in identity provider: issues HS256 JWTs. The API trusts only the token's `sub` for identity.
/// Token lifetimes use the real wall clock (not the domain TimeProvider) because JWT validation does.
/// </summary>
public sealed class TokenService(IOptions<AuthOptions> options)
{
    public const string Issuer = "seatres";
    public const string Audience = "seatres";

    public static SymmetricSecurityKey SigningKey(string secret) => new(Encoding.UTF8.GetBytes(secret));

    public (string Token, DateTime ExpiresAt) Issue(string userId, string role)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddHours(options.Value.TokenHours);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Subject = new ClaimsIdentity([new Claim("sub", userId), new Claim("role", role)]),
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = new SigningCredentials(SigningKey(options.Value.JwtKey), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
