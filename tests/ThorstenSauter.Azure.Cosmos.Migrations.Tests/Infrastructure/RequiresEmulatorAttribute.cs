namespace ThorstenSauter.Azure.Cosmos.Migrations.Tests.Infrastructure;

/// <summary>
///     Marks tests that need the Cosmos DB emulator container. They are skipped when <c>COSMOS_EMULATOR_TESTS</c> is
///     <c>false</c>, which CI sets on Windows runners: those run Windows containers only, and the emulator is a Linux
///     image.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequiresEmulatorAttribute() : SkipAttribute("The Cosmos DB emulator is disabled (COSMOS_EMULATOR_TESTS=false).")
{
    /// <summary>Gets a value indicating whether emulator tests are disabled in this environment.</summary>
    public static bool Disabled =>
        string.Equals(Environment.GetEnvironmentVariable("COSMOS_EMULATOR_TESTS"), "false", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(Disabled);
}
