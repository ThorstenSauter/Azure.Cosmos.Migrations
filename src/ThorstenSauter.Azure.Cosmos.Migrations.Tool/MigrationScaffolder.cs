using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tool;

/// <summary>
///     Writes a new migration class with the next ID.
/// </summary>
internal static partial class MigrationScaffolder
{
    /// <summary>
    ///     Creates an EF-style ID, <c>yyyyMMddHHmmss_Name</c> in UTC, which sorts after every migration added earlier
    ///     and doesn't collide between branches the way a running number does. Older numeric IDs such as <c>0018</c>
    ///     sort before it.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="name">The normalized migration name.</param>
    /// <param name="existingIds">The IDs of the known migrations.</param>
    /// <returns>The new ID.</returns>
    /// <exception cref="ToolException">The new ID would not sort after every existing one.</exception>
    internal static string CreateId(DateTimeOffset now, string name, IReadOnlyCollection<string> existingIds)
    {
        var id = $"{now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}_{name}";
        var latest = existingIds.Max(StringComparer.Ordinal);
        if (latest is not null && string.CompareOrdinal(latest, id) >= 0)
        {
            throw new ToolException(
                $"The new ID \"{id}\" would sort before the existing \"{latest}\", so it would never run in order. " +
                "Check the system clock, or pass --id.");
        }

        return id;
    }

    /// <summary>Parses the JSON the executable prints for <c>list</c>.</summary>
    /// <param name="json">The JSON array.</param>
    /// <returns>The migrations, in ID order.</returns>
    internal static List<ListedMigration> ParseList(string json)
    {
        using var document = JsonDocument.Parse(json);
        return
        [
            .. document.RootElement.EnumerateArray().Select(e => new ListedMigration(
                e.GetProperty("id").GetString()!,
                e.GetProperty("type").GetString() ?? string.Empty))
        ];
    }

    /// <summary>
    ///     Chooses where a new migration goes: <paramref name="outputDirectory" /> when given, else next to the latest
    ///     existing migration (derived from its namespace below <paramref name="rootNamespace" />), else
    ///     <c>Migrations</c>.
    /// </summary>
    /// <param name="outputDirectory">The folder given on the command line, if any.</param>
    /// <param name="rootNamespace">The project's root namespace.</param>
    /// <param name="existing">The existing migrations, in ID order.</param>
    /// <returns>The folder relative to the project, and the namespace for the new class.</returns>
    internal static (string Directory, string Namespace) ChooseLocation(
        string? outputDirectory,
        string rootNamespace,
        IReadOnlyList<ListedMigration> existing)
    {
        if (outputDirectory is not null)
        {
            return (outputDirectory, NamespaceFor(rootNamespace, outputDirectory));
        }

        var latestNamespace = existing.Count > 0 ? NamespaceOf(existing[^1].Type) : null;
        if (latestNamespace == rootNamespace)
        {
            return (".", rootNamespace);
        }

        if (latestNamespace is not null && latestNamespace.StartsWith(rootNamespace + ".", StringComparison.Ordinal))
        {
            return (latestNamespace[(rootNamespace.Length + 1)..].Replace('.', '/'), latestNamespace);
        }

        return ("Migrations", NamespaceFor(rootNamespace, "Migrations"));
    }

    /// <summary>Validates <paramref name="name" /> and makes it a PascalCase identifier.</summary>
    /// <param name="name">The name given on the command line.</param>
    /// <returns>The class-name suffix.</returns>
    /// <exception cref="ToolException">The name is not a valid identifier.</exception>
    internal static string NormalizeName(string name)
    {
        if (!IdentifierPattern().IsMatch(name))
        {
            throw new ToolException($"\"{name}\" is not a valid C# identifier; use something like OrdersAddDeletedAt.");
        }

        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>Renders the migration source file.</summary>
    /// <param name="namespaceName">The namespace.</param>
    /// <param name="className">The class name.</param>
    /// <param name="id">The migration ID.</param>
    /// <param name="name">The migration name, used as the description.</param>
    /// <returns>The C# source.</returns>
    internal static string Render(string namespaceName, string className, string id, string name) =>
        $$"""
          using Microsoft.Azure.Cosmos;
          using ThorstenSauter.Azure.Cosmos.Migrations;

          namespace {{namespaceName}};

          /// <summary>
          ///     Describe what this migration changes and why.
          /// </summary>
          /// <remarks>
          ///     <para>
          ///         <b>Up</b>: what it changes. Keep it safe to run again after a partial failure.
          ///     </para>
          ///     <para>
          ///         <b>Down</b>: how it is undone, leaving data changed since then intact.
          ///     </para>
          /// </remarks>
          internal sealed class {{className}} : CosmosMigration
          {
              public override string Id => "{{id}}";

              public override string Description => "{{name}}";

              public override Task UpAsync(CancellationToken cancellationToken)
              {
                  // Database.GetContainer(name) opens a container; QueryAllAsync streams query results page by page.
                  throw new NotImplementedException();
              }

              public override Task DownAsync(CancellationToken cancellationToken)
              {
                  throw new NotImplementedException();
              }
          }

          """;

    /// <summary>Combines the root namespace and the output folder into the migration's namespace.</summary>
    /// <param name="rootNamespace">The project's root namespace.</param>
    /// <param name="outputDirectory">The folder relative to the project.</param>
    /// <returns>The namespace.</returns>
    internal static string NamespaceFor(string rootNamespace, string outputDirectory)
    {
        var segments = outputDirectory
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .Select(s => IdentifierPattern().IsMatch(s) ? s : "_" + NonIdentifierCharacters().Replace(s, "_"));

        return string.Join('.', [rootNamespace, .. segments]);
    }

    /// <summary>Gets the class name of a migration from its full type name.</summary>
    /// <param name="typeFullName">The full type name, as <c>list</c> prints it.</param>
    /// <returns>The class name without namespace.</returns>
    internal static string ClassNameOf(string typeFullName)
    {
        var outer = typeFullName.Split('+')[^1];
        return outer[(outer.LastIndexOf('.') + 1)..];
    }

    /// <summary>
    ///     Finds the source file of a migration: <c>&lt;id&gt;.cs</c> (as <c>add</c> names it) or
    ///     <c>&lt;ClassName&gt;.cs</c>, anywhere under the project except <c>bin</c> and <c>obj</c>.
    /// </summary>
    /// <param name="projectDirectory">The project directory.</param>
    /// <param name="migration">The migration.</param>
    /// <returns>The matching files.</returns>
    internal static List<string> FindSourceFiles(string projectDirectory, ListedMigration migration)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase)
        {
            migration.Id + ".cs",
            ClassNameOf(migration.Type) + ".cs",
        };

        return
        [
            .. Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                .Where(f => names.Contains(Path.GetFileName(f)))
                .Where(f => !Path.GetRelativePath(projectDirectory, f)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj"))
        ];
    }

    private static string? NamespaceOf(string typeFullName)
    {
        // Nested types print as Outer+Inner; the namespace ends at the last dot before any '+'.
        var outer = typeFullName.Split('+')[0];
        var dot = outer.LastIndexOf('.');
        return dot < 0 ? null : outer[..dot];
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierCharacters();
}

/// <summary>A migration as the executable lists it.</summary>
/// <param name="Id">The migration ID.</param>
/// <param name="Type">The full type name.</param>
internal sealed record ListedMigration(string Id, string Type);
