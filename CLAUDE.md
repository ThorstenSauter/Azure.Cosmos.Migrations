# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Versioned, reversible migrations for Azure Cosmos DB for NoSQL, published to nuget.org as two packages that always
ship with the same version:

- `ThorstenSauter.Azure.Cosmos.Migrations` (`src/ThorstenSauter.Azure.Cosmos.Migrations`): the library. It contains
  the `CosmosMigration` base class, `CosmosMigrationRunner`, the record store and lease (`Storage/`), and
  `Design/MigrationsCommandExecutor`, the command layer the tool runs.
- `ThorstenSauter.Azure.Cosmos.Migrations.Tool` (`src/...Tool`): `dotnet cosmos-migrations`. It builds (or takes
  `--assembly`) a migrations project and re-executes `src/...Tool.Exec` (`cosmos-migrations-exec.dll`, shipped under
  `tools/.../exec/`) with `dotnet exec --depsfile <project>.deps.json --runtimeconfig <project>.runtimeconfig.json`.
  This is the dotnet-ef pattern. `bundle` instead generates a host project under the migrations project's `obj/`
  (`MigrationBundle`) that references it and calls `MigrationsCommandExecutor`, and publishes it as a single file,
  like `dotnet ef migrations bundle`.
- Migration IDs from `add` are EF-style, `yyyyMMddHHmmss_Name`, and the class is named after the migration. Targets
  resolve through `CosmosMigrationRunner.ResolveTarget` (full ID, timestamp or name). Older numeric IDs (`0001`) sort
  before timestamps and keep working. Never change how IDs sort.
- The executor writes logs to stderr; stdout carries only command output (`list`, `status --json`), which the tool
  parses.

It was extracted from [ewlv-website](https://github.com/ThorstenSauter/ewlv-website)'s former `EWLV.DbMigrator`, which
is its first consumer.

## Commands

```bash
dotnet build
dotnet test                       # TUnit on Microsoft.Testing.Platform; needs Docker for the emulator tests
COSMOS_EMULATOR_TESTS=false dotnet test   # unit tests only
dotnet pack Azure.Cosmos.Migrations.slnx -c Release -o artifacts/pkg -p:Version=0.1.0-dev.1
```

## Rules for changes

- **The document format is a contract.** Applied-migration records are `{"id", "Description", "AppliedAt"}` in a
  container partitioned by `/id`, and the lease is the document `id = "__lock"` in that container. Existing databases
  (ewlv's included) were written in this shape, some of them by Newtonsoft before this package existed. Never rename
  these properties. Reads and writes go through the stream API with `MigrationsJsonContext` (System.Text.Json source
  generation), never through the client's serializer.
- **The exec must stay dependency-free.** `cosmos-migrations-exec` runs inside the consumer's dependency graph, so it
  references only the library and loads nothing else. It checks library compatibility before touching a library
  type: same major version, and the same minor while on 0.x (`LibraryVersion` in `Tool.Exec/Program.cs`). Anything
  the inner process needs belongs in the library, whose own dependencies (Cosmos SDK, Azure.Identity,
  Logging.Console) reach consumers transitively.
- **Everything targets the lowest supported .NET (`net10.0`).** The exec runs on the migrations project's runtime,
  so it can't target a newer framework than any consumer; the tool rolls forward (`RollForward=Major`) and newer
  consumers use the `net10.0` library. Raise the floor only as a breaking change.
- **The tool project never references the library.** The outer process stays version-independent of the consumer;
  it only builds, resolves paths and starts `dotnet exec`.
- `buildTransitive/ThorstenSauter.Azure.Cosmos.Migrations.props` sets `GenerateRuntimeConfigurationFiles`, so class
  libraries get the `runtimeconfig.json` the re-exec needs. `tests/SampleMigrations` imports it by hand, because a
  `ProjectReference` doesn't apply NuGet build assets.
- **Every behaviour change gets a test.** Selection logic, validation, connection-string parsing and scaffolding are
  unit-tested. The runner, lease and tool end to end run against the Linux emulator (`vnext-latest`, Testcontainers,
  one container per session, one database per test).
- **Lease logic** uses `TimeProvider`; tests drive renewal with `FakeTimeProvider`. Every lease write is
  ETag-conditional. Don't add unconditional writes to the `__lock` document.
- Code style: `.editorconfig`, copied from ewlv-website. Warnings are errors (`Directory.Build.props`). CI runs
  `dotnet format --verify-no-changes`; run `dotnet format` before committing.
- **Public API baseline (library only):** `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`. New members go into
  `Unshipped`, and move to `Shipped` at release time. Removing or changing a shipped entry is a breaking change:
  bump the major version (or the minor while on 0.x) and label the PR `breaking`. A minor bump on 0.x also makes
  older tools refuse the new library, so release both packages together, as the release workflow does.
  `PackageValidationBaselineVersion` in the library project also compares every pack with the last stable release on
  nuget.org. A deliberate break fails `dotnet pack`: record it with `-p:ApiCompatGenerateSuppressionFile=true` and
  commit the `CompatibilitySuppressions.xml`. After each stable release, raise the baseline and delete that file.

## Packages and dependencies

- Central package management: versions live in `Directory.Packages.props`, and `PackageReference`s carry no version.
- **Lock files:** each project's `packages.lock.json` is committed. CI (`CI=true`) restores in locked mode. After
  changing a package, run `dotnet restore` and commit the lock files.
- **SBOM:** [Sbom](https://github.com/SimonCropp/Sbom) embeds an SPDX 3.0.1 SBOM in both packages;
  `pack-and-publish` attests it with the DLLs.
- `NuGet.config` maps everything to nuget.org; neither package may depend on a private feed.
- Renovate (`.github/renovate.json`, extending `local>ThorstenSauter/renovate-config`) updates packages, the SDK in
  `global.json`, and the actions, regenerating `.github/workflows/actions.lock` with each action update.

## CI and releases

- `.github/workflows/ci.yml` runs `ThorstenSauter/dotnet-actions/build-and-test` on Ubuntu (all tests) and on Windows
  (`COSMOS_EMULATOR_TESTS=false`, because Windows runners can't run the Linux emulator). It also runs a format
  check and a pack-only run of `pack-and-publish` on the solution. The `main` ruleset requires only the `CI passed`
  gate job, so add every new job to its `needs:`.
- `build-and-test` builds with `-p:ArtifactsPath=...`, so output paths differ from a local build. Never hard-code
  `bin/<Configuration>` paths: the tool collects the exec through its `ProjectReference` (`OutputItemType`), and the
  tests run the tool from their own output directory.
- `.github/workflows/release.yml`: on a published GitHub release, a `test` job checks that the tagged commit is on
  `main` and tests again with only `contents: read`. A separate `publish` job then runs `pack-and-publish` on the
  solution, which pushes both packages to nuget.org via trusted publishing (OIDC, `nuget` environment, which needs
  a reviewer's approval, `NUGET_USER` environment variable). No API key is stored anywhere. Keep tests and anything
  else that runs third-party code out of the job holding `id-token: write`.
- Only the admin can create `v*` tags (the `release tags` ruleset), and releases are immutable.
- Actions settings allow only GitHub-owned actions plus `ThorstenSauter/*`, `NuGet/login` and
  `danielpalme/ReportGenerator-GitHub-Action`; allow a new third-party action there before using it. A **Require
  lockfile** policy (Settings → Actions → Policies) blocks any workflow whose actions aren't in the lockfile.
- The release tag is the package version: `v0.1.0-rc.1` becomes `0.1.0-rc.1`.
- Release notes are generated from PR labels (`.github/release.yml`). Label every PR.
- Workflows reference actions by full version tag (`@v7.0.1`), not by SHA. `.github/workflows/actions.lock`
  (`gh actions-lock`) holds the commit SHAs for those and for the actions `dotnet-actions` uses internally, and the
  runner runs only the locked commits. After adding a workflow or changing a `uses:` line, run `gh actions-lock` and
  commit the lockfile. The `actions-lock` CI job runs `gh actions-lock --verify`.
- Commit messages use Conventional Commits.
