# ThorstenSauter.Azure.Cosmos.Migrations

Versioned, reversible migrations for [Azure Cosmos DB for NoSQL](https://learn.microsoft.com/azure/cosmos-db/nosql/).
You write each migration as a C# class with `UpAsync` and `DownAsync`. The `dotnet cosmos-migrations` tool applies
them in order, records each one in a container, and rolls them back again, much like `dotnet ef` does for relational
databases. EF Core migrations don't support the Cosmos DB provider.

- **Ordered and recorded**: migrations run in ordinal order of their `Id`. Each applied migration gets a document
  in a `migrations` container (partition key `/id`).
- **Reversible**: `rollback` undoes the last migration, or everything after a given one.
- **One runner at a time**: applying and rolling back hold a lease in the same container. A second runner waits for
  it instead of interleaving.
- **Runs your code where it lives**: the tool builds your project and runs its migrations against the project's own
  dependencies, so the migrations can live in a plain class library.

| Package | Purpose |
|---|---|
| `ThorstenSauter.Azure.Cosmos.Migrations` | The library your migrations project references |
| `ThorstenSauter.Azure.Cosmos.Migrations.Tool` | The `dotnet cosmos-migrations` command-line tool |

Both target .NET 10 and run on later versions too.

## Getting started

Create a class library for the migrations, or use an existing project, and reference the library:

```bash
dotnet add package ThorstenSauter.Azure.Cosmos.Migrations
```

Install the tool, preferably in a [tool manifest](https://learn.microsoft.com/dotnet/core/tools/local-tools-how-to-use)
so its version is pinned in the repository:

```bash
dotnet new tool-manifest   # once per repository
dotnet tool install ThorstenSauter.Azure.Cosmos.Migrations.Tool
```

Keep the tool and the package on the same version. The tool refuses to run against a project that references an
incompatible version of the library: a different major version, or a different minor version while on 0.x.

### Writing a migration

Scaffold one:

```bash
dotnet cosmos-migrations add OrdersAddDeletedAt --project src/MyApp.Migrations
# Added 20261016213045_OrdersAddDeletedAt: src/MyApp.Migrations/Migrations/20261016213045_OrdersAddDeletedAt.cs
```

Like EF Core, the ID is a UTC timestamp plus the name, and the class is named after the migration. Two branches that
each add a migration get different IDs, so they merge without renumbering. The new file goes next to the latest
existing migration, in the same folder and namespace. For the first one it goes in `Migrations/`, and
`--output-dir <folder>` overrides either. Made a mistake? `dotnet cosmos-migrations remove` deletes the latest
migration again, as long as it isn't applied. Then fill it in:

```csharp
internal sealed class OrdersAddDeletedAt : CosmosMigration
{
    public override string Id => "20261016213045_OrdersAddDeletedAt";

    public override string Description => "Orders: add DeletedAt";

    public override async Task UpAsync(CancellationToken cancellationToken)
    {
        var orders = Database.GetContainer("orders");
        var query = new QueryDefinition("SELECT c.id, c.CustomerId FROM c WHERE NOT IS_DEFINED(c.DeletedAt)");

        await foreach (var order in QueryAllAsync<OrderKey>(orders, query, cancellationToken))
        {
            await orders.PatchItemAsync<object>(
                order.Id,
                new PartitionKey(order.CustomerId),
                [PatchOperation.Add<object?>("/DeletedAt", null)],
                cancellationToken: cancellationToken);
        }
    }

    public override async Task DownAsync(CancellationToken cancellationToken)
    {
        // Undo only what UpAsync did, and leave data changed since then alone.
    }

    private sealed record OrderKey([property: JsonProperty("id")] string Id, string CustomerId);
}
```

A migration needs a parameterless constructor, which may be non-public. The runner gives it `Client`, `DatabaseName`
and `Database`. `QueryAllAsync` streams query results page by page. Some rules for writing migrations:

- **IDs sort as strings.** `add`'s timestamps always sort in creation order. Hand-written IDs need a fixed width
  (`0001`, not `1`). Older zero-padded numbers sort before timestamps, so a project can switch styles without
  renaming anything. Never change or reuse an ID once a migration has been applied anywhere.
- **A migration merged late still runs.** If a branch's migration sorts before one that is already applied, `migrate`
  applies it anyway and logs a warning, so make sure the two don't depend on each other.
- **Make `UpAsync` safe to run again.** If a migration fails halfway, it isn't recorded, and the next run starts it
  over. Filtering on the state you are changing (`NOT IS_DEFINED(...)` above) is usually enough.
- **Make `DownAsync` idempotent and narrow.** It should only undo what `UpAsync` did.

## Running migrations

```bash
dotnet cosmos-migrations migrate  --project src/MyApp.Migrations --connection-string "<...>" --database mydb
dotnet cosmos-migrations migrate  --target OrdersAddDeletedAt ...  # apply up to and including that migration
dotnet cosmos-migrations rollback ...                              # roll back the most recent migration
dotnet cosmos-migrations rollback --target 20261016213045 ...      # roll back everything after that migration
dotnet cosmos-migrations rollback --target 0000 ...                # roll back everything
dotnet cosmos-migrations status [--json] ...                       # applied and pending migrations
dotnet cosmos-migrations list [--json]                             # the project's migrations, without connecting
dotnet cosmos-migrations remove ...                                # delete the latest migration's file (not applied)
dotnet cosmos-migrations bundle --runtime linux-x64 --self-contained   # a single executable for deployments
```

A `--target` is a migration's full ID or, for an EF-style ID, just its timestamp or its name. `remove` checks the
database first; pass the connection options, or `--force` to skip the check.

| Option | Meaning |
|---|---|
| `--project <path>` | The project holding the migrations: a `.csproj` or its directory. Defaults to the current directory. The tool builds it first unless you pass `--no-build`. |
| `--assembly <dll>` | A built or published migrations assembly, used instead of a project (e.g. in a deployment pipeline). |
| `--configuration <name>` | The build configuration. Defaults to `Debug`. |
| `--connection-string <value>` | The connection string. Falls back to `COSMOS_MIGRATIONS_CONNECTION_STRING`. |
| `--connection-name <name>` | Reads the connection string from `ConnectionStrings__<name>`, the variable .NET Aspire sets. |
| `--database <name>` | The database to migrate. Falls back to `COSMOS_MIGRATIONS_DATABASE`. |
| `--container <name>` | The container recording applied migrations. Defaults to `migrations`; it is created if missing. |
| `--lock-timeout <seconds>` | How long to wait for another runner's lease. Defaults to 300. |
| `--verbose` | Debug logging. |

The exit code is `0` on success, `1` when a migration or the connection fails, and `2` for invalid arguments.

### Connection strings

- **An endpoint without a key**, either bare (`https://myaccount.documents.azure.com:443/`) or as
  `AccountEndpoint=...`: the tool signs in with Microsoft Entra ID through `DefaultAzureCredential`. In CI, set
  `AZURE_TOKEN_CREDENTIALS=AzureCliCredential` (or another credential) to skip the probing. The identity needs a
  Cosmos DB data-plane role that can read and write items, and create the migrations container on the first run.
- **`AccountEndpoint=...;AccountKey=...`**: key authentication.
- **The emulator**: a loopback or plain-HTTP endpoint switches to gateway mode, limited to that endpoint, so
  container port mappings work. On loopback only, `DisableServerCertificateValidation=True` is honoured.

### In a deployment pipeline

Build a **bundle** with the release and run it at deployment time. A bundle is one executable holding your
migrations, their dependencies and the runner, much like `dotnet ef migrations bundle`. The deployment machine needs
neither the SDK nor the tool, and with `--self-contained` not even a .NET runtime. A release then always applies
exactly the migrations it was built with:

```bash
dotnet cosmos-migrations bundle --project src/MyApp.Migrations --runtime linux-x64 --self-contained --output out/
# later, at deployment time
./out/cosmos-migrations-bundle migrate --connection-name cosmos-db --database mydb
```

The bundle takes the same commands and connection options as the tool (`migrate`, `rollback`, `status`, `list`;
`--help` lists them). `bundle` defaults to the `Release` configuration and the current platform. It can build for
another one, e.g. `linux-arm64` on Windows. It generates a small host project under the migrations project's `obj/`
folder and publishes it as a single file.

Without a bundle, publish the project and run the published assembly with the tool instead:

```bash
dotnet publish src/MyApp.Migrations -c Release -o out/migrations
dotnet tool restore
dotnet cosmos-migrations migrate --assembly out/migrations/MyApp.Migrations.dll --connection-name cosmos-db --database mydb
```

### With .NET Aspire

Aspire can run the tool as a [.NET tool resource](https://aspire.dev/integrations/dotnet/dotnet-tool-resources/):

```csharp
#pragma warning disable ASPIREDOTNETTOOL
builder.AddDotnetTool("db-migrator", "ThorstenSauter.Azure.Cosmos.Migrations.Tool")
    .WithToolVersion("0.1.0")
    .WithArgs("migrate", "--project", migrationsProjectPath, "--connection-name", "cosmos-db", "--database", "mydb")
    .WithReference(cosmos)
    .WaitFor(cosmos);
```

### From your own code

The runner is an ordinary class if you'd rather host it yourself:

```csharp
var runner = new CosmosMigrationRunner(
    cosmosClient,
    new CosmosMigrationOptions { DatabaseName = "mydb" },
    CosmosMigrationRunner.DiscoverMigrations(typeof(OrdersAddDeletedAt).Assembly),
    logger);

var result = await runner.MigrateAsync();
if (!result.Succeeded)
{
    throw new InvalidOperationException($"Migration {result.FailedMigrationId} failed.", result.Error);
}
```

To give a console app the same commands as the tool, call
`MigrationsCommandExecutor.RunAsync(typeof(Program).Assembly, args)`.

## How it works

**Records.** Every applied migration is a document `{"id": "0001", "Description": "...", "AppliedAt": "..."}` in
the migrations container. These documents are read and written with System.Text.Json, independent of the
serializer your `CosmosClient` uses. Records written in this shape by other tools are recognised.

**Lease.** Before applying or rolling back, the runner creates a document with the reserved ID `__lock` that holds
an owner and an expiry. The runner renews it every third of the lease duration (2 minutes by default). Every write
is conditional on the document's ETag, so only one runner can hold the lease. A second runner waits up to
`--lock-timeout`, and takes the lease over once it has expired, which covers a runner that crashed. If the lease
can't be renewed, the runner stops before doing anything else. `status` takes no lease.

Expiry is compared against the local clock. Machines whose clocks differ by more than the lease duration can both
hold the lease.

**Running your code.** The tool builds your project (or takes `--assembly`) and starts a small helper with
`dotnet exec --depsfile <project>.deps.json --runtimeconfig <project>.runtimeconfig.json`. This is the approach
`dotnet ef` uses. Your migrations and their dependencies load exactly as your application would load them. The
library package makes sure a class library gets the `runtimeconfig.json` this needs. A bundle skips all of this: its
generated host references your project directly and runs the same command layer.

## Verifying a package

Every release is built and published by this repository's `release.yml` workflow via nuget.org trusted publishing,
with a signed GitHub build provenance attestation. nuget.org adds its own repository signature to each package, so
the downloaded `.nupkg` itself won't match. Verify the assemblies and the embedded SBOM inside it instead, which are
attested too:

```bash
id=thorstensauter.azure.cosmos.migrations
version=<version>
curl -sSLo pkg.nupkg "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"
unzip -q -o pkg.nupkg -d pkg
gh attestation verify pkg/lib/net10.0/ThorstenSauter.Azure.Cosmos.Migrations.dll \
  --repo ThorstenSauter/Azure.Cosmos.Migrations
gh attestation verify pkg/_manifest/spdx_3.0/manifest.spdx.json \
  --repo ThorstenSauter/Azure.Cosmos.Migrations
```

For the tool, use `id=thorstensauter.azure.cosmos.migrations.tool` and verify
`pkg/tools/net10.0/any/dotnet-cosmos-migrations.dll` and `pkg/tools/net10.0/any/exec/cosmos-migrations-exec.dll`.

## Building

```bash
dotnet test
```

This requires the SDK pinned in `global.json` and a running Docker engine. The tests start the Linux Cosmos DB
emulator (`vnext-latest`) with Testcontainers.

## License

[MIT](LICENSE)
