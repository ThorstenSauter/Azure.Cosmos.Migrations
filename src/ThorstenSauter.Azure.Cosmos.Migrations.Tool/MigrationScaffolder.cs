using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThorstenSauter.Azure.Cosmos.Migrations.Tool;

/// <summary>
///     Writes a new migration class with the next ID.
/// </summary>
internal static partial class MigrationScaffolder
{
    private const int MinimumIdWidth = 4;

    /// <summary>
    ///     Computes the next ID after <paramref name="existingIds" />: one more than the highest, zero-padded to the
    ///     widest existing ID (at least four digits).
    /// </summary>
    /// <param name="existingIds">The IDs of the known migrations.</param>
    /// <returns>The next ID.</returns>
    /// <exception cref="ToolException">An existing ID is not a number.</exception>
    internal static string NextId(IReadOnlyCollection<string> existingIds)
    {
        if (existingIds.FirstOrDefault(id => !id.All(char.IsAsciiDigit)) is { } custom)
        {
            throw new ToolException($"Existing migration ID \"{custom}\" is not a number; pass --id to choose the new one.");
        }

        var width = Math.Max(MinimumIdWidth, existingIds.Select(id => id.Length).DefaultIfEmpty(0).Max());
        var next = existingIds.Select(id => long.Parse(id, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() + 1;
        return next.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
    }

    /// <summary>Parses the JSON the executable prints for <c>list</c>.</summary>
    /// <param name="json">The JSON array.</param>
    /// <returns>The migration IDs.</returns>
    internal static List<string> ParseIds(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetString()!)];
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonIdentifierCharacters();
}
