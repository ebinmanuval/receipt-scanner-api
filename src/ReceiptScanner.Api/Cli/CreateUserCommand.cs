using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Core.Auth;
using ReceiptScanner.Infrastructure.Data;
using ReceiptScanner.Infrastructure.Data.Entities;

namespace ReceiptScanner.Api.Cli;

/// <summary>
/// <c>dotnet run --project src/ReceiptScanner.Api -- create-user --username ebin [--email e@x.com]
/// [--first-name Ebin] [--last-name M] [--staff] [--superuser] [--password-stdin]</c>
/// <para>
/// Prompts for the password twice (hidden) unless <c>--password-stdin</c> is
/// given or stdin is redirected, in which case it reads one line. The hash is
/// Django's pbkdf2_sha256, so the user can also log in to the Django app.
/// There's no signup endpoint; this is the only way to add users.
/// </para>
/// </summary>
public static partial class CreateUserCommand
{
    public const string Name = "create-user";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        if (!TryParse(args, out var o, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Usage: create-user --username NAME [--email E] [--first-name F] [--last-name L] [--staff] [--superuser] [--password-stdin]");
            return 2;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReceiptScannerDbContext>();

        // Same comparison the database (and Django's login) uses: the column's
        // collation, case-insensitive on a default MySQL install.
        if (await db.Users.AnyAsync(u => u.Username == o.Username))
        {
            Console.Error.WriteLine($"A user called '{o.Username}' already exists.");
            return 1;
        }

        var password = o.PasswordStdin || Console.IsInputRedirected ? Console.In.ReadLine() : PromptTwice();
        if (password is null)
        {
            Console.Error.WriteLine("No password given.");
            return 1;
        }
        if (PasswordProblem(password, o.Username) is { } problem)
        {
            Console.Error.WriteLine(problem);
            return 1;
        }

        var user = new User
        {
            Username = o.Username,
            Email = o.Email,
            FirstName = o.FirstName,
            LastName = o.LastName,
            IsStaff = o.Staff || o.Superuser,
            IsSuperuser = o.Superuser,
            IsActive = true,
            Password = DjangoPasswordHasher.Hash(password),
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        Console.WriteLine($"Created user '{user.Username}' (id {user.Id}){(user.IsSuperuser ? ", superuser" : user.IsStaff ? ", staff" : "")}.");
        return 0;
    }

    internal sealed record Options(string Username, string Email, string FirstName, string LastName,
        bool Staff, bool Superuser, bool PasswordStdin);

    internal static bool TryParse(string[] args, out Options options, out string error)
    {
        options = null!;
        string? username = null, email = "", first = "", last = "";
        bool staff = false, superuser = false, stdin = false;

        for (var i = 1; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--username": username = Next(); break;
                case "--email": email = Next() ?? ""; break;
                case "--first-name": first = Next() ?? ""; break;
                case "--last-name": last = Next() ?? ""; break;
                case "--staff": staff = true; break;
                case "--superuser": superuser = true; break;
                case "--password-stdin": stdin = true; break;
                default:
                    error = $"Unknown option '{args[i]}'.";
                    return false;
            }
        }

        username = username?.Trim();
        if (string.IsNullOrEmpty(username))
        {
            error = "--username is required.";
            return false;
        }
        // Django's UnicodeUsernameValidator: letters, digits and @ . + - _ ; max 150.
        if (username.Length > 150 || !UsernamePattern().IsMatch(username))
        {
            error = "Username may contain only letters, numbers and @/./+/-/_ characters (150 max).";
            return false;
        }
        if (email.Length > 254 || first.Length > 150 || last.Length > 150)
        {
            error = "Email is limited to 254 characters, first and last name to 150.";
            return false;
        }

        options = new Options(username, email.Trim(), first.Trim(), last.Trim(), staff, superuser, stdin);
        error = "";
        return true;
    }

    /// <summary>Django's default validators, minus the common-passwords list.</summary>
    internal static string? PasswordProblem(string password, string username)
    {
        if (password.Length < 8) return "This password is too short. It must contain at least 8 characters.";
        if (password.All(char.IsDigit)) return "This password is entirely numeric.";
        if (string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
            return "The password is too similar to the username.";
        return null;
    }

    private static string? PromptTwice()
    {
        var first = ReadHidden("Password: ");
        var second = ReadHidden("Password (again): ");
        if (first != second)
        {
            Console.Error.WriteLine("Passwords don't match.");
            return null;
        }
        return first;
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    [GeneratedRegex(@"^[\w.@+-]+$")]
    private static partial Regex UsernamePattern();
}
