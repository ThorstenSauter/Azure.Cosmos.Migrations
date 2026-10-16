using System.Diagnostics;
using System.Text;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tool;

/// <summary>
///     Runs the <c>dotnet</c> CLI as a child process.
/// </summary>
internal static class DotnetCli
{
    /// <summary>
    ///     Gets the <c>dotnet</c> executable: the one that launched this tool when the SDK says so, else the one on
    ///     <c>PATH</c>.
    /// </summary>
    internal static string Executable =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : "dotnet";

    /// <summary>Runs <c>dotnet</c> with output captured.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The exit code, standard output and standard error.</returns>
    internal static async Task<CapturedRun> CaptureAsync(
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = CreateStartInfo(arguments) };
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        return new CapturedRun(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>
    ///     Runs <c>dotnet</c> sharing this console, so its output and Ctrl+C reach the user directly. Cancelling waits
    ///     for the child to finish what it is doing instead of killing it.
    /// </summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="environment">Extra environment variables for the child.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(arguments) };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            process.StartInfo.Environment[name] = value;
        }

        process.Start();

        // Not cancellable on purpose: Ctrl+C reaches the child through the shared console and it stops cleanly,
        // releasing its migration lease.
        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }

    private static ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(Executable) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

/// <summary>The result of a captured <c>dotnet</c> run.</summary>
/// <param name="ExitCode">The exit code.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error.</param>
internal sealed record CapturedRun(int ExitCode, string Output, string Error);
