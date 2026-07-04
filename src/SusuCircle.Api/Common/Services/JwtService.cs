using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SusuCircle.Api.Common.Models;

namespace SusuCircle.Api.Common.Services;

// CHANGED: added GenerateAccessToken(Member) — same signing key, issuer,
// audience, and expiry config as the Admin overload, but with a "role": "member"
// claim (the Admin overload implicitly has no role claim at all, which reads
// as "admin" by omission for now — see note below on tightening this later).
public interface IJwtService
{
    string GenerateAccessToken(Admin admin);
    string GenerateAccessToken(Member member);
    string GenerateRefreshToken();
    ClaimsPrincipal? GetPrincipalFromExpiredToken(string token);
}

public class JwtService(IConfiguration config) : IJwtService
{
    public string GenerateAccessToken(Admin admin)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Secret"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, admin.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, admin.Email),
            new Claim("name", admin.Name),
            new Claim("role", "admin"),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(Convert.ToDouble(config["Jwt:ExpiryMinutes"] ?? "60")),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // NEW — mirrors GenerateAccessToken(Admin) exactly: same key, issuer,
    // audience, expiry config. Differs only in the claims: uses the member's
    // own id/email/name, adds "role": "member" (so downstream authorization
    // can distinguish token types), and adds "circleId" since most member-
    // scoped endpoints will need to know which circle the token belongs to
    // without a separate lookup.
    public string GenerateAccessToken(Member member)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Secret"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, member.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, member.Email ?? string.Empty),
            new Claim("name", member.Name),
            new Claim("role", "member"),
            new Claim("circleId", member.CircleId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(Convert.ToDouble(config["Jwt:ExpiryMinutes"] ?? "60")),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateRefreshToken()
    {
        var bytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    public ClaimsPrincipal? GetPrincipalFromExpiredToken(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            ValidIssuer = config["Jwt:Issuer"],
            ValidAudience = config["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Secret"]!))
        };

        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
        }
        catch { return null; }
    }
}