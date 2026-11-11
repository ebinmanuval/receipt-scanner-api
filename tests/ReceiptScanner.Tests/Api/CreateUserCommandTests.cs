using ReceiptScanner.Api.Auth;
using ReceiptScanner.Api.Cli;

namespace ReceiptScanner.Tests.Api;

public class CreateUserCommandTests
{
    [Fact]
    public void Parses_options()
    {
        Assert.True(CreateUserCommand.TryParse(
            ["create-user", "--username", "ebin", "--email", "e@x.com", "--first-name", "Ebin", "--superuser"],
            out var o, out _));

        Assert.Equal("ebin", o.Username);
        Assert.Equal("e@x.com", o.Email);
        Assert.Equal("Ebin", o.FirstName);
        Assert.True(o.Superuser);
        Assert.False(o.PasswordStdin);
    }

    [Theory]
    [InlineData("create-user")]                                  // no username
    [InlineData("create-user", "--username", "has space")]
    [InlineData("create-user", "--username", "x", "--bogus")]
    public void Rejects_bad_arguments(params string[] args)
    {
        Assert.False(CreateUserCommand.TryParse(args, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("short", "u")]
    [InlineData("1234567890", "u")]
    [InlineData("ebinebin", "EBINEBIN")]
    public void Weak_passwords_are_refused(string password, string username) =>
        Assert.NotNull(CreateUserCommand.PasswordProblem(password, username));

    [Fact]
    public void A_reasonable_password_is_accepted() =>
        Assert.Null(CreateUserCommand.PasswordProblem("long enough pass", "ebin"));

    [Theory]
    [InlineData("secret-key", "secret-key", true)]
    [InlineData("secret-key", "secret-kez", false)]
    [InlineData("secret-key", "secret", false)]
    public void Api_key_compare(string provided, string expected, bool matches) =>
        Assert.Equal(matches, ApiKeyAuthenticationHandler.Matches(provided, expected));
}
