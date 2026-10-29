# TestPrune

F# test impact analysis tool. Uses FSharp.Compiler.Service to build a symbol
dependency graph from AST analysis, then determines which tests are affected
by code changes.

## Build & Test

```bash
dotnet build
dotnet test
# or run tests directly:
dotnet exec tests/TestPrune.Tests/bin/Debug/net10.0/TestPrune.Tests.dll
```

## Mise Tasks

```bash
mise run build          # Build the solution
mise run test           # Run tests with coverage
mise run check          # Run all checks with auto-fix
mise run ci             # Run all CI checks (no auto-fix)
mise run ci-linux       # Run GitHub Actions' CI jobs in a local Linux container (Docker)
mise run format         # Format with Fantomas
mise run lint           # Run FSharpLint
mise run example        # Run test-prune against example solution
mise run example-build  # Build the example solution
mise run docs           # Generate API documentation
mise run coverage-check   # Check per-file coverage thresholds
mise run coverage-ratchet # Tighten coverage thresholds to current values
mise run sync-docs        # Sync README.md to docs/index.md
mise run sync-docs-check  # Check docs sync
mise run dead-code      # Find unreachable production code
mise run dead-code-tests # Find unreachable test code
mise run bench          # Run benchmarks with dotnet-trace profiling
mise run bench-raw      # Run benchmarks without profiling (JSON metrics)
mise run release        # Tag a release based on API changes
mise run release-alpha  # Tag an alpha pre-release
```

## Merging to main

Before every push to `main` (a merge, a re-land, a revert) run BOTH `mise run ci` and
`mise run ci-linux`, and push only when both exit 0. `ci-linux` runs GitHub Actions' jobs
(`build`, `lint-project`) in a local Linux container with the workflow's SDK band, coverage
floors, `CI`/`GITHUB_ACTIONS` environment and a non-root user, and leaves logs and coverage in
`.ci-linux/`. macOS `ci` cannot see Linux-only reds, and in FsHotWatch, which shares this CI,
all 14 inspected CI reds of 2026-10-05..28 were Linux-only.

- `CI_LINUX_JOBS=build mise run ci-linux` runs one job; `mise run ci-linux -- <checkout>`
  runs another checkout.
- It refuses to run (exit 2) when a shared reusable workflow it transcribes changed upstream.
  Update `scripts/ci-linux/steps.sh` to match (FsHotWatch carries the same file), then the
  pinned blob ids in `scripts/ci-linux.sh`.
- After pushing, still watch the commit's GitHub CI run to the end; on red, revert first.

## Project Structure

- `src/TestPrune.Core/` — Core library: AST analysis, SQLite graph, symbol diffing, impact selection
- `src/TestPrune/` — CLI tool (index, run, status, dead-code)
- `src/TestPrune.Falco/` — Falco route-based integration test filtering extension
- `src/TestPrune.Trace/`, `src/TestPrune.Trace.Recorder/`, `src/TestPrune.Trace.Cli/` — recorded
  per-test traces (preview, record only): weaver and trace store, in-process recorder,
  `test-prune-traces` measurement verbs
- `tests/TestPrune.Tests/` — All tests (xUnit v3 + MTP v2)
- `examples/SampleSolution/` — Example F# solution for smoke testing

## Conventions

- F# code formatted with Fantomas, linted with FSharpLint
- 4-space indentation
- VCS: jj (Jujutsu), not git
- Tests use xUnit v3 with Microsoft Testing Platform v2
- Package versions managed centrally in `Directory.Packages.props` (CPM) with
  floats enabled and transitive pinning on. FSharp.Core and FCS are EXACT pins, never floats:
  a packed nuspec floor is what restore resolved, so a float leaks the newest release into
  consumers' floors (AUTOMATION-1358). `mise run package-floors` checks real pack output.
- NU1605 suppressed across projects (FSharp.Core version mismatch with SDK)

## Shared Tooling

Uses NuGet tools from michaelglass/MichaelsWackyFsPackageTools:
- `coverageratchet` — per-file coverage enforcement with automatic threshold ratcheting
- `syncdocs` — README-to-docs section syncing
- `fssemantictagger` — semantic versioning with API change detection

CI uses reusable GitHub workflows from the same repo.

The FsHotWatch CLI pin is a release boundary, not a local-development override. See
`docs/runbooks/fshotwatch-pin-upgrade.md` before changing it: publish the required CLI
fix first, pin that exact released version second, and only then run the full gate.

## Package Publishing

Packable projects use separate semantic release tags unless noted:
- `core-v*` — TestPrune.Core + CLI
- `falco-v*` — TestPrune.Falco
- `analyzers-v*` — TestPrune.Analyzers
- `sql-v*` — TestPrune.Sql
- `sqlhydra-v*` — TestPrune.SqlHydra (release only after its referenced TestPrune.Sql version is available from NuGet)
- `trace-v*` — TestPrune.Trace + TestPrune.Trace.Recorder + TestPrune.Trace.Cli (one tag: the
  weaver calls recorder methods by name, so the three never ship apart; after Core)

Use `mise run release`, which invokes the semantic tagger once per level of the
ProjectReference DAG and waits for every package of a level to become restorable
before tagging the next: Analyzers and Core; then Falco, Sql and Trace; then
SqlHydra. `ReleaseOrchestrationTests` derives those levels from
`semantic-tagger.json` and fails when `mise.toml` disagrees. Never replace it with an unscoped
`fssemantictagger release`; independent tag workflows cannot enforce cross-tag
NuGet dependency ordering.
