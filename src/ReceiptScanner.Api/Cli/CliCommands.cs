namespace ReceiptScanner.Api.Cli;

/// <summary>
/// Maintenance commands run as <c>dotnet run --project src/ReceiptScanner.Api -- &lt;command&gt;</c>.
/// They share the app's config and DI but don't start the web server.
/// </summary>
public static class CliCommands
{
    public static bool Matches(string[] args) =>
        DbCommands.Matches(args) || (args.Length >= 1 && args[0] == CreateUserCommand.Name);

    public static Task<int> RunAsync(IServiceProvider services, string[] args) => args[0] switch
    {
        CreateUserCommand.Name => CreateUserCommand.RunAsync(services, args),
        _ => DbCommands.RunAsync(services, args),
    };
}
