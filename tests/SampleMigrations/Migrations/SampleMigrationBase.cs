using ThorstenSauter.Azure.Cosmos.Migrations;

namespace SampleMigrations.Migrations;

/// <summary>Abstract base classes are not migrations; discovery must skip them.</summary>
internal abstract class SampleMigrationBase : CosmosMigration;
