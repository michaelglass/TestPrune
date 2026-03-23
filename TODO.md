# TestPrune OSS — Remaining TODOs

## Before OSS extraction (done when addressed)
- [x] Fix CLI ProjectLoader (use Ionide.ProjInfo)
- [x] README / docs
- [x] RouteMapping uses Route.createMatcher instead of string matching

## Should do
- [ ] Unit tests for FalcoRouteExtension (only integration-tested via build pipeline)
- [ ] SymbolDiff: content hashing instead of line-range comparison (comment shifts cause false positives for functions below)
- [ ] `selectAffectedTests` graceful fallback when `getScriptOptions` produces bad results

## Nice to have
- [ ] NuGet package metadata (descriptions, license, repo URL) for TestPrune.Core, TestPrune.Falco
- [ ] CI workflow for the extracted repo
- [ ] Example project showing integration (standalone, without Intelligence monolith)
