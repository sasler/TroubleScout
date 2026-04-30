using FluentAssertions;
using TroubleScout.Services;
using Xunit;

namespace TroubleScout.Tests.Services;

public class SecretRedactorTests
{
    private const string M = SecretRedactor.Mask;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_NullOrEmpty_ReturnsInputUnchanged(string? input)
    {
        SecretRedactor.Redact(input).Should().Be(input ?? string.Empty);
    }

    [Fact]
    public void Redact_PlainText_LeavesItAlone()
    {
        var s = "The server is healthy and CPU is at 12%.";
        SecretRedactor.Redact(s).Should().Be(s);
    }

    // -------- GitHub tokens --------

    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("gho_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghu_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghs_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("ghr_abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Redact_GitHubClassicTokens_AreRedacted(string token)
    {
        var input = $"token={token} please";
        SecretRedactor.Redact(input).Should().NotContain(token);
        SecretRedactor.Redact(input).Should().Contain(M);
    }

    [Fact]
    public void Redact_GitHubFineGrainedPat_IsRedacted()
    {
        var token = "github_pat_11ABCDEFG0_abcdefghijklmnopqrstuvwxyz0123456789";
        SecretRedactor.Redact($"GH_TOKEN={token}").Should().NotContain(token);
    }

    // -------- AWS keys --------

    [Theory]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ASIAIOSFODNN7EXAMPLE")]
    public void Redact_AwsAccessKeyIds_AreRedacted(string key)
    {
        SecretRedactor.Redact($"id={key}").Should().NotContain(key);
    }

    // -------- JWT --------

    [Fact]
    public void Redact_JwtToken_IsRedacted()
    {
        // Plausible-looking JWT (header.payload.signature, all base64url).
        var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var input = $"Authorization header had {jwt} attached";
        SecretRedactor.Redact(input).Should().NotContain(jwt);
        SecretRedactor.Redact(input).Should().Contain(M);
    }

    // -------- Bearer --------

    [Fact]
    public void Redact_BearerToken_KeepsLabelButMasksValue()
    {
        var input = "Authorization: Bearer abc123def456ghi789";
        var redacted = SecretRedactor.Redact(input);

        redacted.Should().Contain("Bearer");
        redacted.Should().NotContain("abc123def456ghi789");
        redacted.Should().Contain(M);
    }

    [Fact]
    public void Redact_BareWordBearer_IsLeftAlone()
    {
        // The word "Bearer" by itself or followed by a short word must not be
        // greedily redacted — only the credential after it.
        var s = "The Bearer of bad news is here.";
        SecretRedactor.Redact(s).Should().Be(s);
    }

    // -------- URL userinfo --------

    [Fact]
    public void Redact_UrlWithUserInfo_MasksPasswordOnly()
    {
        var input = "Connecting to https://alice:s3cret-pa55@example.com/db";
        var redacted = SecretRedactor.Redact(input);

        redacted.Should().Contain("alice:");
        redacted.Should().Contain("@example.com/db");
        redacted.Should().NotContain("s3cret-pa55");
        redacted.Should().Contain(M);
    }

    [Fact]
    public void Redact_UrlWithoutUserInfo_IsLeftAlone()
    {
        var s = "See https://example.com/path?x=1 for details.";
        SecretRedactor.Redact(s).Should().Be(s);
    }

    // -------- Connection strings --------

    [Theory]
    [InlineData("Server=db;Password=hunter2;Database=foo;", "hunter2")]
    [InlineData("Server=db;Pwd=hunter2;Database=foo;", "hunter2")]
    [InlineData("AccountKey=abcd1234efgh==;", "abcd1234efgh==")]
    [InlineData("SharedAccessKey=verysecretvalue;", "verysecretvalue")]
    [InlineData("Server=db;Password=\"hunter2\";Database=foo;", "hunter2")]
    [InlineData("Server=db;Pwd='hunter2';Database=foo;", "hunter2")]
    [InlineData("Password=\"value with spaces and ; semicolon\"", "value with spaces and ; semicolon")]
    public void Redact_ConnectionStringSecrets_AreRedacted(string input, string secret)
    {
        var redacted = SecretRedactor.Redact(input);
        redacted.Should().NotContain(secret);
        redacted.Should().Contain(M);
    }

    [Fact]
    public void Redact_QuotedConnectionStringSecret_PreservesSurroundingQuotes()
    {
        var redacted = SecretRedactor.Redact("Server=db;Password=\"hunter2\";Database=foo;");

        redacted.Should().Be($"Server=db;Password=\"{M}\";Database=foo;");
    }

    // -------- Generic key=value --------

    [Theory]
    [InlineData("api_key=abcdef1234567890")]
    [InlineData("apikey=abcdef1234567890")]
    [InlineData("access_token=abcdef1234567890")]
    [InlineData("refresh_token=abcdef1234567890")]
    [InlineData("client_secret=abcdef1234567890")]
    [InlineData("PRIVATE_KEY=abcdef1234567890")]
    [InlineData("TOKEN=abcdef1234567890")]
    [InlineData("secret: abcdef1234567890")]
    [InlineData("MY_API_KEY=abcdef1234567890")]
    public void Redact_KeyValueSecrets_AreRedacted(string input)
    {
        var redacted = SecretRedactor.Redact(input);
        redacted.Should().NotContain("abcdef1234567890");
        redacted.Should().Contain(M);
    }

    [Fact]
    public void Redact_QuotedSecretValue_KeepsQuotesAndMasks()
    {
        var input = "api_key=\"abcdef1234567890\"";
        var redacted = SecretRedactor.Redact(input);

        redacted.Should().NotContain("abcdef1234567890");
        redacted.Should().Contain($"\"{M}\"",
            "quoted secrets must remain quoted so the structure (e.g. JSON) parses afterwards.");
    }

    [Theory]
    [InlineData("background_color=red")]
    [InlineData("port=8080")]
    [InlineData("hostname=db01")]
    [InlineData("status=healthy")]
    public void Redact_NonSecretKeyValues_AreLeftAlone(string input)
    {
        SecretRedactor.Redact(input).Should().Be(input);
    }

    [Fact]
    public void Redact_VeryShortValue_IsNotMistakenForSecret()
    {
        // The KeyValueSecretPattern requires {4,} chars to keep noise like
        // `token=ok` or `api_key=12` from being misclassified — these are
        // rarely real secrets and false positives hurt readability.
        var input = "token=ok";
        SecretRedactor.Redact(input).Should().Be(input);
    }

    // -------- Composition --------

    [Fact]
    public void Redact_MultipleSecretsInOneString_AllAreRedacted()
    {
        var input = "Use ghp_abcdefghijklmnopqrstuvwxyz0123456789 with "
                    + "Authorization: Bearer eyJxxxxxxxxxxxxxxxxxx and "
                    + "url https://u:p4ssw0rd-x@host/path "
                    + "(api_key=abcdef1234567890)";
        var redacted = SecretRedactor.Redact(input);

        redacted.Should().NotContain("ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        redacted.Should().NotContain("eyJxxxxxxxxxxxxxxxxxx");
        redacted.Should().NotContain("p4ssw0rd-x");
        redacted.Should().NotContain("abcdef1234567890");
    }

    // -------- ContainsSecret --------

    [Fact]
    public void ContainsSecret_PlainText_ReturnsFalse()
    {
        SecretRedactor.ContainsSecret("CPU at 12%").Should().BeFalse();
    }

    [Fact]
    public void ContainsSecret_NullOrEmpty_ReturnsFalse()
    {
        SecretRedactor.ContainsSecret(null).Should().BeFalse();
        SecretRedactor.ContainsSecret(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void ContainsSecret_DetectsMatchingPatterns()
    {
        SecretRedactor.ContainsSecret("api_key=abcdef1234567890").Should().BeTrue();
        SecretRedactor.ContainsSecret("ghp_abcdefghijklmnopqrstuvwxyz0123456789").Should().BeTrue();
        SecretRedactor.ContainsSecret("https://user:pw_value@host").Should().BeTrue();
    }
}
