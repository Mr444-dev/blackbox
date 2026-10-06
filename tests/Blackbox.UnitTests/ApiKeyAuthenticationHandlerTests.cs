using Blackbox.Api.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;

namespace Blackbox.UnitTests;

public sealed class ApiKeyAuthenticationHandlerTests
{
    private const string ValidKey = "unit-test-key-with-at-least-32-characters";

    [Fact]
    public async Task AuthenticateAsync_WithValidKey_Succeeds()
    {
        var result = await AuthenticateAsync(ValidKey, ValidKey);

        result.Succeeded.Should().BeTrue();
        result.Principal?.Identity?.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_WithWrongKey_Fails()
    {
        var result = await AuthenticateAsync(ValidKey, "another-key-with-at-least-32-characters");

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_WithoutHeader_ReturnsNoResult()
    {
        var result = await AuthenticateAsync(ValidKey, null);

        result.None.Should().BeTrue();
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(string configuredKey, string? suppliedKey)
    {
        var options = new ApiKeyAuthenticationOptions { ApiKey = configuredKey };
        var handler = new ApiKeyAuthenticationHandler(
            new FixedOptionsMonitor<ApiKeyAuthenticationOptions>(options),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);
        var context = new DefaultHttpContext();
        if (suppliedKey is not null)
            context.Request.Headers["X-API-Key"] = suppliedKey;

        await handler.InitializeAsync(
            new AuthenticationScheme("ApiKey", "API key", typeof(ApiKeyAuthenticationHandler)),
            context);

        return await handler.AuthenticateAsync();
    }

    private sealed class FixedOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
        where TOptions : class
    {
        public TOptions CurrentValue => value;

        public TOptions Get(string? name) => value;

        public IDisposable OnChange(Action<TOptions, string?> listener) => EmptyDisposable.Instance;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
