using System.Text.Json;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tool;

/// <summary>
///     The built migrations assembly the tool runs, and how to start it in that assembly's dependency context.
/// </summary>
/// <param name="AssemblyPath">The migrations assembly.</param>
/// <param name="ProbingPath">The NuGet global packages folder, needed for a build (not publish) output.</param>
/// <param name="ProjectDirectory">The project directory, when resolved from a project.</param>
/// <param name="RootNamespace">The project's root namespace, when resolved from a project.</param>
internal sealed record MigrationsTarget(
    string AssemblyPath,
    string? ProbingPath,
    string? ProjectDirectory,
    string? RootNamespace)
{
    /// <summary>The executable shipped next to the tool that runs inside the migrations project's context.</summary>
    internal static string ExecPath => Path.Combine(AppContext.BaseDirectory, "exec", "cosmos-migrations-exec.dll");

    /// <summary>
    ///     Resolves the migrations assembly from <paramref name="assembly" /> when given, else by building
    ///     <paramref name="project" /> (or the single project in the current directory).
    /// </summary>
    /// <param name="project">A project file or a directory holding one.</param>
    /// <param name="assembly">A prebuilt or published migrations assembly.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="noBuild">Whether to skip building the project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The target.</returns>
    /// <exception cref="ToolException">The project or assembly cannot be resolved or built.</exception>
    internal static async Task<MigrationsTarget> ResolveAsync(
        string? project,
        string? assembly,
        string configuration,
        bool noBuild,
        CancellationToken cancellationToken)
    {
        if (assembly is not null)
        {
            if (project is not null)
            {
                throw new ToolException("Pass either --project or --assembly, not both.");
            }

            var assemblyPath = Path.GetFullPath(assembly);
            if (!File.Exists(assemblyPath))
            {
                throw new ToolException($"Assembly '{assemblyPath}' not found.");
            }

            return new MigrationsTarget(assemblyPath, await GlobalPackagesFolderAsync(cancellationToken), null, null);
        }

        var projectPath = FindProject(project);
        if (!noBuild)
        {
            await Console.Error.WriteLineAsync($"Building {Path.GetFileName(projectPath)}...");
            var build = await DotnetCli.CaptureAsync(
                ["build", projectPath, "--configuration", configuration, "--nologo", "-v", "q"],
                cancellationToken);

            if (build.ExitCode != 0)
            {
                await Console.Error.WriteLineAsync(build.Output);
                await Console.Error.WriteLineAsync(build.Error);
                throw new ToolException("Build failed.");
            }
        }

        var properties = await GetPropertiesAsync(
            projectPath,
            configuration,
            ["TargetPath", "NuGetPackageRoot", "RootNamespace", "ProjectDir"],
            cancellationToken);

        var targetPath = properties.GetValueOrDefault("TargetPath");
        if (string.IsNullOrEmpty(targetPath))
        {
            throw new ToolException(
                $"Could not determine the output of {Path.GetFileName(projectPath)}. Projects with several target " +
                "frameworks are not supported.");
        }

        if (!File.Exists(targetPath))
        {
            throw new ToolException($"'{targetPath}' does not exist. Build the project or drop --no-build.");
        }

        return new MigrationsTarget(
            targetPath,
            NullIfEmpty(properties.GetValueOrDefault("NuGetPackageRoot")) ?? await GlobalPackagesFolderAsync(cancellationToken),
            NullIfEmpty(properties.GetValueOrDefault("ProjectDir")),
            NullIfEmpty(properties.GetValueOrDefault("RootNamespace")));
    }

    /// <summary>
    ///     Builds the <c>dotnet exec</c> arguments that run <see cref="ExecPath" /> in the migrations assembly's
    ///     dependency context.
    /// </summary>
    /// <param name="innerArguments">The command and options for the executable.</param>
    /// <returns>The arguments for <c>dotnet</c>.</returns>
    /// <exception cref="ToolException">The assembly lacks its deps.json or runtimeconfig.json.</exception>
    internal List<string> ExecArguments(IEnumerable<string> innerArguments)
    {
        var directory = Path.GetDirectoryName(AssemblyPath)!;
        var name = Path.GetFileNameWithoutExtension(AssemblyPath);
        var depsFile = Path.Combine(directory, name + ".deps.json");
        var runtimeConfig = Path.Combine(directory, name + ".runtimeconfig.json");

        if (!File.Exists(depsFile) || !File.Exists(runtimeConfig))
        {
            throw new ToolException(
                $"'{name}.deps.json' and '{name}.runtimeconfig.json' must sit next to the migrations assembly. The " +
                "ThorstenSauter.Azure.Cosmos.Migrations package generates them for the project that references it " +
                "directly; rebuild (or republish) after adding it.");
        }

        List<string> arguments = ["exec", "--depsfile", depsFile, "--runtimeconfig", runtimeConfig];
        if (ProbingPath is not null)
        {
            arguments.AddRange(["--additionalprobingpath", ProbingPath]);
        }

        arguments.Add(ExecPath);
        arguments.Add(AssemblyPath);
        arguments.AddRange(innerArguments);
        return arguments;
    }

    internal static string FindProject(string? project)
    {
        var path = Path.GetFullPath(project ?? Directory.GetCurrentDirectory());
        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            throw new ToolException($"Project '{path}' not found.");
        }

        var projects = Directory.GetFiles(path, "*.csproj");
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new ToolException($"No project file in '{path}'. Pass --project or --assembly."),
            _ => throw new ToolException($"Several project files in '{path}'. Pass --project <csproj>."),
        };
    }

    internal static async Task<Dictionary<string, string>> GetPropertiesAsync(
        string projectPath,
        string configuration,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["msbuild", projectPath, "-nologo", $"-p:Configuration={configuration}"];
        arguments.AddRange(names.Select(n => $"-getProperty:{n}"));

        var run = await DotnetCli.CaptureAsync(arguments, cancellationToken);
        if (run.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync(run.Output);
            throw new ToolException($"Could not evaluate {Path.GetFileName(projectPath)}.");
        }

        // Several -getProperty switches print {"Properties": {...}}.
        using var json = JsonDocument.Parse(run.Output);
        return json.RootElement.GetProperty("Properties")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static async Task<string?> GlobalPackagesFolderAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured)
        {
            return configured;
        }

        var run = await DotnetCli.CaptureAsync(["nuget", "locals", "global-packages", "--list"], cancellationToken);
        const string Prefix = "global-packages:";
        var line = run.Output.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase));

        return run.ExitCode == 0 && line is not null ? NullIfEmpty(line[Prefix.Length..].Trim()) : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>A user-facing tool error; the message is printed without a stack trace.</summary>
/// <param name="message">The error message.</param>
internal sealed class ToolException(string message) : Exception(message);
