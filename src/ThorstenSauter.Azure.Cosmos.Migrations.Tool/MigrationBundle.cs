using System.Runtime.InteropServices;
using System.Security;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tool;

/// <summary>
///     Builds a migrations bundle: a single executable holding a project's migrations, their dependencies and the
///     runner, so a deployment can apply migrations without the .NET SDK or this tool (and, self-contained, without a
///     .NET runtime). The pattern is <c>dotnet ef migrations bundle</c>: a generated host project references the
///     migrations project and is published as a single file.
/// </summary>
internal static class MigrationBundle
{
    /// <summary>The bundle's file name without extension.</summary>
    internal const string Name = "cosmos-migrations-bundle";

    /// <summary>
    ///     Generates the host project under the migrations project's intermediate directory, publishes it and copies
    ///     the executable to <paramref name="output" />.
    /// </summary>
    /// <param name="project">The migrations project, or a directory holding it.</param>
    /// <param name="configuration">The build configuration.</param>
    /// <param name="runtime">The target runtime identifier; the current one by default.</param>
    /// <param name="selfContained">Whether to include the .NET runtime.</param>
    /// <param name="output">The output file or directory; the current directory by default.</param>
    /// <param name="force">Whether to overwrite an existing output file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The path of the bundle.</returns>
    /// <exception cref="ToolException">The project cannot be evaluated or published, or the output exists.</exception>
    internal static async Task<string> BuildAsync(
        string? project,
        string configuration,
        string? runtime,
        bool selfContained,
        string? output,
        bool force,
        CancellationToken cancellationToken)
    {
        runtime ??= RuntimeInformation.RuntimeIdentifier;
        var outputPath = OutputPath(output, runtime);
        if (File.Exists(outputPath) && !force)
        {
            throw new ToolException($"'{outputPath}' already exists. Pass --force to overwrite it.");
        }

        var projectPath = MigrationsTarget.FindProject(project);
        var properties = await MigrationsTarget.GetPropertiesAsync(
            projectPath,
            configuration,
            ["TargetFramework", "AssemblyName", "ProjectDir", "BaseIntermediateOutputPath"],
            cancellationToken);

        var targetFramework = properties.GetValueOrDefault("TargetFramework");
        if (string.IsNullOrEmpty(targetFramework))
        {
            throw new ToolException(
                $"Could not determine the target framework of {Path.GetFileName(projectPath)}. Projects with several " +
                "target frameworks are not supported.");
        }

        // MSBuild reports the default as "obj\" even on Linux, where a backslash is an ordinary file name character.
        var intermediate = Path.Combine(
            properties.GetValueOrDefault("ProjectDir") ?? Path.GetDirectoryName(projectPath)!,
            properties.GetValueOrDefault("BaseIntermediateOutputPath") is { Length: > 0 } obj
                ? obj.Replace('\\', Path.DirectorySeparatorChar)
                : "obj");
        var hostDirectory = Path.Combine(intermediate, Name);
        if (Directory.Exists(hostDirectory))
        {
            Directory.Delete(hostDirectory, recursive: true);
        }

        Directory.CreateDirectory(hostDirectory);
        var hostProject = Path.Combine(hostDirectory, Name + ".csproj");
        await File.WriteAllTextAsync(
            hostProject,
            RenderProject(targetFramework, projectPath, runtime, selfContained),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(hostDirectory, "Program.cs"),
            RenderProgram(properties.GetValueOrDefault("AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath)),
            cancellationToken);

        var publishDirectory = Path.Combine(hostDirectory, "publish");
        await Console.Error.WriteLineAsync(
            $"Building the migrations bundle for {runtime}{(selfContained ? " (self-contained)" : string.Empty)}...");
        var publish = await DotnetCli.CaptureAsync(
            [
                // The runtime and self-containment live in the host project rather than on this command line: as
                // global properties they would also apply to the migrations project, restoring it for that runtime,
                // which its lock file rejects in locked mode (NU1004) or would otherwise be rewritten for.
                "publish", hostProject,
                "--configuration", configuration,
                "--output", publishDirectory,
                "--nologo",
            ],
            cancellationToken);

        if (publish.ExitCode != 0)
        {
            await Console.Error.WriteLineAsync(publish.Output);
            await Console.Error.WriteLineAsync(publish.Error);
            throw new ToolException("Building the bundle failed.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.Copy(Path.Combine(publishDirectory, Path.GetFileName(ExecutableName(runtime))), outputPath, overwrite: true);
        return outputPath;
    }

    /// <summary>Renders the host project.</summary>
    /// <param name="targetFramework">The migrations project's target framework.</param>
    /// <param name="migrationsProject">The absolute path of the migrations project.</param>
    /// <param name="runtime">The target runtime identifier.</param>
    /// <param name="selfContained">Whether to include the .NET runtime.</param>
    /// <returns>The project file.</returns>
    internal static string RenderProject(
        string targetFramework,
        string migrationsProject,
        string runtime,
        bool selfContained) =>
        $"""
         <!-- <auto-generated> by dotnet cosmos-migrations bundle. Regenerated on every run; do not edit. -->
         <Project Sdk="Microsoft.NET.Sdk">
           <PropertyGroup>
             <OutputType>Exe</OutputType>
             <TargetFramework>{SecurityElement.Escape(targetFramework)}</TargetFramework>
             <RuntimeIdentifier>{SecurityElement.Escape(runtime)}</RuntimeIdentifier>
             <SelfContained>{(selfContained ? "true" : "false")}</SelfContained>
             <AssemblyName>{Name}</AssemblyName>
             <RootNamespace>CosmosMigrationsBundle</RootNamespace>
             <ImplicitUsings>enable</ImplicitUsings>
             <Nullable>enable</Nullable>
             <PublishSingleFile>true</PublishSingleFile>
             <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
             <IsPackable>false</IsPackable>
             <GenerateDocumentationFile>false</GenerateDocumentationFile>
             <!-- The surrounding repository's analyzers, warning policy and locked restore don't apply to generated code. -->
             <RunAnalyzers>false</RunAnalyzers>
             <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
             <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>
             <RestoreLockedMode>false</RestoreLockedMode>
           </PropertyGroup>
           <ItemGroup>
             <ProjectReference Include="{SecurityElement.Escape(migrationsProject)}" />
           </ItemGroup>
         </Project>
         """;

    /// <summary>Renders the host's entry point.</summary>
    /// <param name="migrationsAssembly">The migrations assembly name.</param>
    /// <returns>The C# source.</returns>
    internal static string RenderProgram(string migrationsAssembly) =>
        $$"""
          // <auto-generated> by dotnet cosmos-migrations bundle. Regenerated on every run; do not edit.
          using System.Reflection;
          using ThorstenSauter.Azure.Cosmos.Migrations.Design;

          using var cancellation = new CancellationTokenSource();
          Console.CancelKeyPress += (_, e) =>
          {
              e.Cancel = true;
              cancellation.Cancel();
          };

          return await MigrationsCommandExecutor.RunAsync(Assembly.Load("{{migrationsAssembly}}"), args, cancellation.Token);

          """;

    /// <summary>Gets the executable's file name for <paramref name="runtime" />.</summary>
    /// <param name="runtime">The runtime identifier.</param>
    /// <returns>The file name.</returns>
    internal static string ExecutableName(string runtime) =>
        runtime.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? Name + ".exe" : Name;

    private static string OutputPath(string? output, string runtime)
    {
        if (output is null)
        {
            return Path.Combine(Directory.GetCurrentDirectory(), ExecutableName(runtime));
        }

        var full = Path.GetFullPath(output);
        return Directory.Exists(full) || output.EndsWith('/') || output.EndsWith('\\')
            ? Path.Combine(full, ExecutableName(runtime))
            : full;
    }
}
