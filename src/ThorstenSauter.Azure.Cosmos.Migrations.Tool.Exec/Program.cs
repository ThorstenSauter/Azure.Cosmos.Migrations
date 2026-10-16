using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using ThorstenSauter.Azure.Cosmos.Migrations.Design;

// Usage: cosmos-migrations-exec <migrations.dll> <command> [options]
// Started by dotnet-cosmos-migrations with the migrations project's deps.json and runtimeconfig.json, so the library
// loaded here is the project's own version. Check it before touching any of its types.
if (args.Length < 2)
{
    await Console.Error.WriteLineAsync("usage: cosmos-migrations-exec <migrations.dll> <command> [options]");
    return 2;
}

if (LibraryVersion.Mismatch() is { } mismatch)
{
    await Console.Error.WriteLineAsync(mismatch);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await Execute.RunAsync(LoadMigrations(Path.GetFullPath(args[0])), args[1..], cancellation.Token);

static Assembly LoadMigrations(string path)
{
    try
    {
        // Already listed in the project's deps.json, so load that instance rather than a second copy.
        return AssemblyLoadContext.Default.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
    }
    catch (FileNotFoundException)
    {
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
}

/// <summary>
///     Kept out of the top-level statements so the JIT resolves library types only after the version check passed.
/// </summary>
internal static class Execute
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Task<int> RunAsync(Assembly migrations, string[] args, CancellationToken cancellationToken) =>
        MigrationsCommandExecutor.RunAsync(migrations, args, cancellationToken);
}

/// <summary>
///     Compares the library version this executable was built against with the one the migrations project references.
/// </summary>
internal static class LibraryVersion
{
    private const string LibraryName = "ThorstenSauter.Azure.Cosmos.Migrations";

    /// <summary>
    ///     Returns an error message when the versions are incompatible: a different major version, or, while the major
    ///     version is 0, a different minor version.
    /// </summary>
    /// <returns>The error message, or <see langword="null" /> when compatible.</returns>
    internal static string? Mismatch()
    {
        var expected = typeof(LibraryVersion).Assembly.GetReferencedAssemblies()
            .Single(a => a.Name == LibraryName).Version!;

        Version actual;
        try
        {
            actual = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(LibraryName)).GetName().Version!;
        }
        catch (FileNotFoundException)
        {
            return $"The project does not reference {LibraryName}. Add the package to the project holding the migrations.";
        }

        var compatible = actual.Major == expected.Major && (expected.Major != 0 || actual.Minor == expected.Minor);
        return compatible
            ? null
            : $"The project references {LibraryName} {actual.ToString(3)}, but this tool needs " +
              $"{expected.Major}.{(expected.Major == 0 ? expected.Minor.ToString(System.Globalization.CultureInfo.InvariantCulture) : "x")}. " +
              "Use the tool version that matches the package version.";
    }
}
