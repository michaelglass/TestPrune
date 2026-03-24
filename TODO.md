# TestPrune — Remaining TODOs

## Should do
- [x] Unit tests for FalcoRouteExtension (multi-class, multi-handler scenarios)
- [x] SymbolDiff: content hashing instead of line-range comparison
- [x] `selectAffectedTests` graceful fallback — logs warning and triggers RunAll on parse failure

## Performance — Learnings from Ionide/FSAC Research

### Done
- [x] Project-level caching — skip entire project when no files changed
- [x] File-level caching — skip FCS analysis for unchanged files, load from DB
- [x] Lazy getOptions — defer MSBuild until a file actually needs analysis
- [x] projectCacheSize 25 → 200 (matches FSAC)
- [x] FSharpChecker reuse — callers can pass a checker instance to runIndexWith
- [x] Compilation-order re-checking — files after a changed file are re-analyzed
- [x] Cross-project dependency invalidation — re-index projects whose deps were re-indexed
- [x] Topological project sort — process dependencies before dependents

### Future — Daemon Mode

File watcher daemon would keep FSharpChecker alive (IncrementalBuilder
caches persist across checks), watch for file saves, re-index
incrementally, and serve status/run commands instantly. Requires new
process model (long-running service vs CLI).

FSAC ref: `FSharp.Data.Adaptive` for file watching,
`bypassAdaptiveAndCheckDependenciesForFile` in `AdaptiveServerState.fs`
for incremental re-checking.

### Future — Cross-Project Parallel Analysis

FSharpChecker is thread-safe for concurrent calls with different project
options (verified). Projects at the same topo-sort level could be analyzed
in parallel. `getProjectOptions` still serializes via MSBuild lock, but
FCS analysis after options load can overlap. Would need topo sort to
return levels and handle concurrent DB writes (SQLite WAL supports one
writer at a time, so writes would serialize).

### Done — Snapshot API
- [x] `createProjectSnapshot` and `analyzeSourceWithSnapshot` in AstAnalyzer.fs
- [x] CLI uses snapshot API for indexing (mtime-based file versions)
- [x] ~23% faster cold start on example solution (7.3s vs 9.4s)
- [x] Consumers with long-lived FSharpChecker benefit from FCS-internal caching

### Not Viable

- **enablePartialTypeChecking** — mutually exclusive with
  `keepAssemblyContents = true` (throws `ArgumentException`). We need
  `keepAssemblyContents` for `GetAllUsesOfAllSymbolsInFile` which builds
  the dependency graph. No workaround without a different symbol
  extraction approach.
- **Parallel within a project** — FCS IncrementalBuilder processes files
  sequentially in compilation order. Parallel calls for files in the same
  project redundantly check earlier files.
- **Parse-only for dependencies** — `ParseFile` returns the AST with
  definition names and ranges (verified), but no resolved symbol
  references. We need `GetAllUsesOfAllSymbolsInFile` to know which
  functions call which. Parse-only could extract definitions but not
  the dependency edges that are core to test impact analysis.
