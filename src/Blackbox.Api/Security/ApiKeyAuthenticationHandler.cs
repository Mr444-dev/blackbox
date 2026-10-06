using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Blackbox.Api.Security;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (string.IsNullOrWhiteSpace(Options.ApiKey))
            return Task.FromResult(AuthenticateResult.Fail("API key authentication is not configured."));

        var suppliedKey = Request.Headers["X-API-Key"].ToString();
        if (string.IsNullOrEmpty(suppliedKey))
            return Task.FromResult(AuthenticateResult.NoResult());

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(Options.ApiKey));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        var isValid = CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
        CryptographicOperations.ZeroMemory(expectedHash);
        CryptographicOperations.ZeroMemory(suppliedHash);
        if (!isValid)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "api-client"), new Claim(ClaimTypes.Name, "Blackbox API client")],
            Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
