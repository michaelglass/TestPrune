# Changelog — TestPrune.SqlHydra

## [Unreleased]
- AUTOMATION-558: classify the fully-qualified typed builder members emitted
  for SqlHydra computation-expression custom operations, restoring read/write
  facts that were lost when FCS stopped naming those uses after source keywords.
  Declaring entities and generated-module prefixes are boundary-matched, blank
  prefixes are rejected, and member-shaped dependencies cannot masquerade as tables.
- fix: keep *every* SQL access a symbol performs, not just the first. `extractFacts`
  took `List.tryHead` over a symbol's DSL calls, so a symbol that both reads and
  writes (an upsert-style `select`-then-`insert`) was recorded with only one access
  and the other was silently dropped — and which one survived was decided by SQLite
  row order, since `GetDependenciesFromFile` has no `ORDER BY`. When the dropped
  access was the *write*, the table had no writer at all, its readers got no
  `SharedState` edge, and changing the writer selected **none** of the tests that
  read the table (under-selection). Now exact for the common single-access symbol and
  conservatively (access × table) for a genuinely mixed one; edges are only ever
  added, so no affected test can be dropped.
- chore: initial changelog; bump upstream tool versions
