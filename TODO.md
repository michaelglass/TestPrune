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

### High Impact

#### File watcher daemon mode
FSAC keeps an `FSharpChecker` alive and uses `FileSystemWatcher` per
directory (via `FSharp.Data.Adaptive`). On file change, only that file
and its dependents are re-checked.

For TestPrune, a long-running daemon could:
- Keep FSharpChecker warm (IncrementalBuilder caches persist across checks)
- Watch for file saves and re-index incrementally
- Maintain a ready-to-query dependency graph at all times
- Serve `status` and `run` commands instantly without re-parsing

#### Compilation-order-aware re-checking
F# files compile sequentially — file N depends on 1..N-1 but not N+1..M.
FSAC exploits this: when file K changes, only files K..M need re-checking.

Currently safe to skip because the build step runs first — if a changed
file breaks dependents, build fails before indexing. But for daemon mode
(no build step), compilation-order awareness would be needed.

FSAC ref: `bypassAdaptiveAndCheckDependenciesForFile` in
`AdaptiveServerState.fs` — `Array.splitAt idx` on source files, then
`Async.parallel75` for files after the changed one.

#### FSharpChecker reuse across runs
New `FSharpChecker` per `index` invocation means cold caches every time.
FCS maintains `IncrementalBuilder` per project — reusing the checker (via
daemon) would make re-indexing near-instant because the type-checker
environment for preceding files is already built.

FSAC settings: `projectCacheSize = 200` (we use 25),
`keepAllBackgroundResolutions = true`, `keepAssemblyContents = true`.

### Medium Impact

#### Parallel file analysis within a project
FSAC uses `Async.parallel75` (75% of cores). Our file loop is sequential.
Would help for cold-start indexing only (incremental runs are already fast).

Caveat: FCS IncrementalBuilder benefits from sequential compilation-order
processing. Parallel processing may cause redundant environment building.
Also conflicts with compilation-order invalidation logic (need to know if
earlier files changed before deciding on later ones). Needs benchmarking.

#### Cross-project dependency tracking
When project A changes, projects referencing A may have stale deps. FSAC
finds dependent projects transitively and re-checks their files.

Currently safe because build runs first and symbol names are stable. For
daemon mode, would need addressing.

### Exploratory

#### Transparent Compiler / Snapshot API (FCS 44+)
`FSharpProjectSnapshot` with content-addressable per-file caching. Each
`FSharpFileSnapshot` has a version string; FCS skips unchanged files
internally. Would require FCS upgrade from 43.12.201.

#### Parse-only for definition extraction
`ParseFile` is much faster than `ParseAndCheckFileInProject`. Returns full
AST but no resolved symbols. Two-pass approach: parse all (fast) for
definitions, type-check only changed files for dependency resolution.

#### enablePartialTypeChecking
Uses `.fsi` signature files to skip implementation checking. Mutually
exclusive with `keepAssemblyContents = true` which we need for
`GetAllUsesOfAllSymbolsInFile`.
