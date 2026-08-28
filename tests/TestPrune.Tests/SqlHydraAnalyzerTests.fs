module TestPrune.Tests.SqlHydraAnalyzerTests

open System
open Xunit
open Swensen.Unquote
open TestPrune
open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Sql
open TestPrune.SqlHydra
open TestPrune.Tests.TestHelpers

module ``DSL context classification`` =

    [<Fact>]
    let ``selectTask is read access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "selectTask" = Some Read @>

    [<Fact>]
    let ``selectAsync is read access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "selectAsync" = Some Read @>

    [<Fact>]
    let ``select is read access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "select" = Some Read @>

    [<Fact>]
    let ``insertTask is write access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "insertTask" = Some Write @>

    [<Fact>]
    let ``updateTask is write access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "updateTask" = Some Write @>

    [<Fact>]
    let ``deleteTask is write access`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "deleteTask" = Some Write @>

    [<Fact>]
    let ``unknown context returns None`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "someOtherFunction" = None @>

    [<Theory>]
    [<InlineData("SqlHydra.Query.SelectBuilders.SelectBuilder`2.Where")>]
    [<InlineData("SqlHydra.Query.SelectBuilders.SelectBuilder`2.Select")>]
    let ``typed select builder custom operations are read access`` symbol =
        test <@ SqlHydraAnalyzer.classifyDslContext symbol = Some Read @>

    [<Theory>]
    [<InlineData("SqlHydra.Query.InsertBuilders.InsertBuilder`1.Entity")>]
    [<InlineData("SqlHydra.Query.UpdateBuilders.UpdateBuilder`1.Set")>]
    [<InlineData("SqlHydra.Query.DeleteBuilders.DeleteBuilder`1.Where")>]
    let ``typed mutation builder custom operations are write access`` symbol =
        test <@ SqlHydraAnalyzer.classifyDslContext symbol = Some Write @>

    [<Fact>]
    let ``similarly named custom operation outside SqlHydra is ignored`` () =
        test <@ SqlHydraAnalyzer.classifyDslContext "Other.Query.SelectBuilder.Where" = None @>

    [<Theory>]
    [<InlineData("FakeSqlHydra.Query.SelectBuilders.SelectBuilder`2.Where")>]
    [<InlineData("SqlHydra.Query.SelectBuilders.SelectBuilderFake`2.Where")>]
    [<InlineData("Prefix.SqlHydra.Query.InsertBuilders.InsertBuilder`2.Entity")>]
    [<InlineData("SqlHydra.Query.UpdateBuilders.FakeUpdateBuilder`2.Set")>]
    [<InlineData("SqlHydra.Query.DeleteBuilders.DeleteBuilderFake`1.Where")>]
    [<InlineData("Fake.SqlHydra.Query.selectTask")>]
    let ``near-prefix suffix and Fake declaring entities are ignored`` symbol =
        test <@ SqlHydraAnalyzer.classifyDslContext symbol = None @>

    [<Fact>]
    let ``fully qualified terminal helper with generic arity is classified`` () =
        let result =
            SqlHydraAnalyzer.classifyDslContext "SqlHydra.Query.InsertBuilders.insertTask``3"

        test <@ result = Some Write @>

module ``Table reference parsing`` =

    [<Fact>]
    let ``parses schema-qualified table name`` () =
        let result = SqlHydraAnalyzer.parseTableReference "Generated.public.briefs"
        test <@ result = Some { Schema = "public"; Table = "briefs" } @>

    [<Fact>]
    let ``parses table name without schema`` () =
        let result = SqlHydraAnalyzer.parseTableReference "Generated.dbo.users"
        test <@ result = Some { Schema = "dbo"; Table = "users" } @>

    [<Fact>]
    let ``returns None for non-matching pattern`` () =
        let result = SqlHydraAnalyzer.parseTableReference "SomeOther.Module"
        test <@ result = None @>

    [<Fact>]
    let ``handles deeply nested generated module`` () =
        let result = SqlHydraAnalyzer.parseTableReference "MyDb.Generated.public.articles"

        test
            <@
                result = Some
                    { Schema = "public"
                      Table = "articles" }
            @>

    [<Fact>]
    let ``prefix-aware parser requires exact schema-table suffix`` () =
        let exact =
            SqlHydraAnalyzer.parseTableReferenceUnder "Generated" "MyDb.Generated.public.articles"

        let memberShaped =
            SqlHydraAnalyzer.parseTableReferenceUnder "Generated" "MyDb.Generated.public.articles.title"

        let nearPrefix =
            SqlHydraAnalyzer.parseTableReferenceUnder "Generated" "MyDb.NotGenerated.public.articles"

        let emptySchema =
            SqlHydraAnalyzer.parseTableReferenceUnder "Generated" "MyDb.Generated..articles"

        let emptyTable =
            SqlHydraAnalyzer.parseTableReferenceUnder "Generated" "MyDb.Generated.public."

        test
            <@
                exact = Some
                    { Schema = "public"
                      Table = "articles" }
            @>

        test <@ memberShaped = None @>
        test <@ nearPrefix = None @>
        test <@ emptySchema = None @>
        test <@ emptyTable = None @>

module ``SqlHydraExtension graph analysis`` =

    [<Fact>]
    let ``detects read when function calls selectTask and uses SqlHydra table type`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Queries.getArticles"
                    Kind = Function
                    SourceFile = "src/Queries.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "SqlHydra.Query.selectTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Queries.getArticles"
                    ToSymbol = "SqlHydra.Query.selectTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Queries.getArticles"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "articles" @>
        test <@ facts[0].Access = Read @>

    [<Fact>]
    let ``detects write when function calls insertTask and uses SqlHydra table type`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Commands.createArticle"
                    Kind = Function
                    SourceFile = "src/Commands.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "SqlHydra.Query.insertTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Commands.createArticle"
                    ToSymbol = "SqlHydra.Query.insertTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.Length = 1 @>
        test <@ facts[0].Table = "articles" @>
        test <@ facts[0].Access = Write @>

    [<Fact>]
    let ``produces SharedState edges when reader and writer exist`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Queries.getArticles"
                    Kind = Function
                    SourceFile = "src/Queries.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "Commands.createArticle"
                    Kind = Function
                    SourceFile = "src/Commands.fs"
                    LineStart = 1
                    LineEnd = 10
                    ContentHash = "b"
                    IsExtern = false }
                  { FullName = "SqlHydra.Query.selectTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "SqlHydra.Query.insertTask"
                    Kind = Function
                    SourceFile = "_extern"
                    LineStart = 0
                    LineEnd = 0
                    ContentHash = ""
                    IsExtern = true }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Queries.getArticles"
                    ToSymbol = "SqlHydra.Query.selectTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Queries.getArticles"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "SqlHydra.Query.insertTask"
                    Kind = Calls
                    Source = "core" }
                  { FromSymbol = "Commands.createArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let extension = SqlHydraExtension("Generated")
        test <@ (extension :> ITestPruneExtension).Name = "SqlHydra" @>

        let edges = (extension :> ITestPruneExtension).AnalyzeEdges store [] ""

        test <@ edges.Length = 1 @>
        test <@ edges[0].Kind = SharedState @>
        test <@ edges[0].Source = "sql-hydra" @>

    [<Fact>]
    let ``ignores functions that use table type but no DSL function`` () =
        let result =
            AnalysisResult.Create(
                [ { FullName = "Helpers.mapArticle"
                    Kind = Function
                    SourceFile = "src/Helpers.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "a"
                    IsExtern = false }
                  { FullName = "Generated.public.articles"
                    Kind = Type
                    SourceFile = "src/DbTypes.fs"
                    LineStart = 1
                    LineEnd = 5
                    ContentHash = "t"
                    IsExtern = false } ],
                [ { FromSymbol = "Helpers.mapArticle"
                    ToSymbol = "Generated.public.articles"
                    Kind = UsesType
                    Source = "core" } ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts.IsEmpty @>

// -----------------------------------------------------------------------------
// Edge scoping: audit against the cross-product over-selection FalcoRoute suffered from,
// and the access-collapse under-selection it turned up.
// -----------------------------------------------------------------------------

/// Terse builders for a symbol graph — the ceremony above obscures the shape.
let private fn (fullName: string) (sourceFile: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = sourceFile
      LineStart = 1
      LineEnd = 10
      ContentHash = fullName
      IsExtern = false }

let private dsl (fullName: string) : SymbolInfo =
    { FullName = fullName
      Kind = Function
      SourceFile = "_extern"
      LineStart = 0
      LineEnd = 0
      ContentHash = ""
      IsExtern = true }

let private table (fullName: string) : SymbolInfo =
    { FullName = fullName
      Kind = Type
      SourceFile = "src/DbTypes.fs"
      LineStart = 1
      LineEnd = 5
      ContentHash = fullName
      IsExtern = false }

let private calls (source: string) (dest: string) : Dependency =
    { FromSymbol = source
      ToSymbol = dest
      Kind = Calls
      Source = "core" }

let private usesType (source: string) (dest: string) : Dependency =
    { FromSymbol = source
      ToSymbol = dest
      Kind = UsesType
      Source = "core" }

[<Collection("FCS-AstAnalyzer")>]
module ``real FCS custom-operation shape`` =

    [<Fact>]
    let ``custom operation resolves to its typed builder member and record type`` () =
        let result =
            TestPrune.Tests.AstAnalyzerTests.analyze
                """
module SqlHydra.Query.SelectBuilders

type articles = { id: int }

type SelectBuilder<'T>() =
    member _.For(source: 'T list, body: 'T -> 'T list) = List.collect body source
    member _.Yield(value: 'T) = [ value ]

    [<CustomOperation("where", MaintainsVariableSpace = true)>]
    member _.Where(source: 'T list, [<ProjectionParameter>] predicate: 'T -> bool) =
        List.filter predicate source

    [<CustomOperation("select")>]
    member _.Select(source: 'T list, [<ProjectionParameter>] projection: 'T -> 'T) =
        List.map projection source

let select = SelectBuilder<articles>()

let query () =
    select {
        for row in [ { id = 1 } ] do
        where (row.id = 1)
        select row
    }
"""

        let queryDeps =
            result.Dependencies
            |> List.filter (fun dependency -> dependency.FromSymbol.EndsWith(".query", StringComparison.Ordinal))

        let whereCall =
            queryDeps
            |> List.tryFind (fun dependency ->
                dependency.Kind = Calls
                && dependency.ToSymbol = "SqlHydra.Query.SelectBuilders.SelectBuilder`1.Where")

        test <@ whereCall.IsSome @>

        test
            <@
                whereCall
                |> Option.bind (fun dependency -> SqlHydraAnalyzer.classifyDslContext dependency.ToSymbol) = Some Read
            @>

        test
            <@
                queryDeps
                |> List.exists (fun dependency ->
                    dependency.Kind = UsesType
                    && dependency.ToSymbol = "SqlHydra.Query.SelectBuilders.articles")
            @>

module ``SqlHydra edge scoping`` =

    [<Fact>]
    let ``detects read from the typed custom-operation symbol emitted by current FCS`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.SelectBuilders.SelectBuilder`2.Where"
                  table "Generated.public.articles" ],
                [ calls "Queries.getArticles" "SqlHydra.Query.SelectBuilders.SelectBuilder`2.Where"
                  usesType "Queries.getArticles" "Generated.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store
        test <@ facts |> List.map (fun f -> f.Table, f.Access) = [ "articles", Read ] @>

    [<Fact>]
    let ``apostrophe-bearing real SqlHydra member name remains a read`` () =
        let result =
            SqlHydraAnalyzer.classifyDslContext "SqlHydra.Query.SelectBuilders.SelectBuilder`2.LeftJoin'"

        test <@ result = Some Read @>

    [<Fact>]
    let ``generated prefix is a dotted module boundary rather than a substring`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.SelectBuilders.SelectBuilder`2.Where"
                  table "NotGenerated.public.articles"
                  table "GeneratedFake.public.articles" ],
                [ calls "Queries.getArticles" "SqlHydra.Query.SelectBuilders.SelectBuilder`2.Where"
                  usesType "Queries.getArticles" "NotGenerated.public.articles"
                  usesType "Queries.getArticles" "GeneratedFake.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        test <@ SqlHydraExtension.extractFacts "Generated" store |> List.isEmpty @>

    [<Fact>]
    let ``table member-shaped dependency is not misread as schema and table`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.SelectBuilders.SelectBuilder`2.Where"
                  table "Generated.public.articles.title" ],
                [ calls "Queries.getArticles" "SqlHydra.Query.SelectBuilders.SelectBuilder`2.Where"
                  usesType "Queries.getArticles" "Generated.public.articles.title" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        test <@ SqlHydraExtension.extractFacts "Generated" store |> List.isEmpty @>

    [<Fact>]
    let ``blank generated module prefix is rejected by constructor and extractor`` () =
        let store = InMemoryStore.fromAnalysisResults []
        raises<ArgumentException> <@ SqlHydraExtension("   ") @>
        raises<ArgumentException> <@ SqlHydraExtension.extractFacts "" store @>

        raises<ArgumentException> <@ SqlHydraAnalyzer.parseTableReferenceUnder "\t" "Generated.public.articles" @>

    /// The FalcoRoute cross-product bug is NOT present here.
    /// `extractFacts` filters each symbol's dependencies to `d.FromSymbol = sym.FullName`,
    /// and the AST attributes a `Calls`/`UsesType` edge to the *enclosing function*, not to
    /// the file. So two queries sharing one source file stay independent: a change to
    /// `getArticles` cannot pull tests that only touch `briefs`. This test pins that —
    /// it fails the moment anyone re-scopes the dependency lookup to the file.
    [<Fact>]
    let ``queries sharing a source file do not smear tables across each other`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  fn "Queries.createBrief" "src/Queries.fs" // SAME file
                  dsl "SqlHydra.Query.selectTask"
                  dsl "SqlHydra.Query.insertTask"
                  table "Generated.public.articles"
                  table "Generated.public.briefs" ],
                [ calls "Queries.getArticles" "SqlHydra.Query.selectTask"
                  usesType "Queries.getArticles" "Generated.public.articles"
                  calls "Queries.createBrief" "SqlHydra.Query.insertTask"
                  usesType "Queries.createBrief" "Generated.public.briefs" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        let triples = facts |> List.map (fun f -> f.Symbol, f.Table, f.Access) |> Set.ofList

        // Each function is scoped to the table IT references — never its file-mate's.
        // A file-level cross-product would additionally emit
        // (getArticles, briefs, _) and (createBrief, articles, _).
        test
            <@
                triples = set
                    [ "Queries.getArticles", "articles", Read
                      "Queries.createBrief", "briefs", Write ]
            @>

    /// A symbol that BOTH reads and writes must keep both accesses. Keeping only the first
    /// DSL access records an upsert (select listed first) as a pure READER, its table ends
    /// up with no writer, `buildEdges` produces ZERO edges, and a change to the upsert
    /// selects none of the tests that read the table.
    [<Fact>]
    let ``symbol that both reads and writes keeps both accesses`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Repo.upsertArticle" "src/Repo.fs"
                  fn "Queries.listArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  dsl "SqlHydra.Query.insertTask"
                  table "Generated.public.articles" ],
                [ // The select is listed FIRST — precisely the ordering under which
                  // `List.tryHead` classified this write-performing symbol as read-only.
                  calls "Repo.upsertArticle" "SqlHydra.Query.selectTask"
                  calls "Repo.upsertArticle" "SqlHydra.Query.insertTask"
                  usesType "Repo.upsertArticle" "Generated.public.articles"
                  calls "Queries.listArticles" "SqlHydra.Query.selectTask"
                  usesType "Queries.listArticles" "Generated.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        let accesses =
            facts
            |> List.filter (fun f -> f.Symbol = "Repo.upsertArticle")
            |> List.map (fun f -> f.Access)
            |> Set.ofList

        test <@ accesses = set [ Read; Write ] @>

        // ...and the edge the dropped write destroyed is back: the reader of `articles`
        // now depends on the upsert that writes it.
        let edges =
            (SqlHydraExtension("Generated") :> ITestPruneExtension).AnalyzeEdges store [] ""

        let pairs = edges |> List.map (fun e -> e.FromSymbol, e.ToSymbol) |> Set.ofList
        test <@ pairs = set [ "Queries.listArticles", "Repo.upsertArticle" ] @>

    /// A single-access symbol stays exactly as precise as before — keeping every access
    /// does not fan a pure reader out into a writer.
    [<Fact>]
    let ``single-access symbol emits exactly one fact per table`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.getArticles" "src/Queries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  table "Generated.public.articles" ],
                [ calls "Queries.getArticles" "SqlHydra.Query.selectTask"
                  usesType "Queries.getArticles" "Generated.public.articles" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        test <@ facts |> List.map (fun f -> f.Table, f.Access) = [ "articles", Read ] @>

    /// A join reads several tables through ONE select: every table gets the read, and no
    /// spurious write appears.
    [<Fact>]
    let ``joined select marks every joined table as read`` () =
        let result =
            AnalysisResult.Create(
                [ fn "Queries.articlesWithBriefs" "src/Queries.fs"
                  dsl "SqlHydra.Query.selectTask"
                  table "Generated.public.articles"
                  table "Generated.public.briefs" ],
                [ calls "Queries.articlesWithBriefs" "SqlHydra.Query.selectTask"
                  usesType "Queries.articlesWithBriefs" "Generated.public.articles"
                  usesType "Queries.articlesWithBriefs" "Generated.public.briefs" ],
                []
            )

        let store = InMemoryStore.fromAnalysisResults [ result ]
        let facts = SqlHydraExtension.extractFacts "Generated" store

        let pairs = facts |> List.map (fun f -> f.Table, f.Access) |> Set.ofList
        test <@ pairs = set [ "articles", Read; "briefs", Read ] @>

module ``SqlHydra under-selection`` =

    /// NO UNDER-SELECTION, end-to-end through the core's recursive reverse-walk:
    /// `testListsArticles` calls `Queries.listArticles`, which reads `articles`;
    /// `Repo.upsertArticle` writes `articles`. Changing the upsert MUST select the test,
    /// reached only via the sql-hydra SharedState edge:
    ///
    ///     upsertArticle ←(SharedState)← listArticles ←(Calls)← testListsArticles
    ///
    /// Drop the write and there is no SharedState edge, so this selects ZERO tests — a
    /// genuinely-affected test silently skipped.
    [<Fact>]
    let ``changing a read-write symbol still selects tests that read the table`` () =
        withDb (fun db ->
            let symbols =
                [ fn "Tests.testListsArticles" "tests/Tests.fs"
                  fn "Queries.listArticles" "src/Queries.fs"
                  fn "Repo.upsertArticle" "src/Repo.fs"
                  dsl "SqlHydra.Query.selectTask"
                  dsl "SqlHydra.Query.insertTask"
                  table "Generated.public.articles" ]

            let coreDeps =
                [ calls "Tests.testListsArticles" "Queries.listArticles"
                  calls "Queries.listArticles" "SqlHydra.Query.selectTask"
                  usesType "Queries.listArticles" "Generated.public.articles"
                  calls "Repo.upsertArticle" "SqlHydra.Query.selectTask"
                  calls "Repo.upsertArticle" "SqlHydra.Query.insertTask"
                  usesType "Repo.upsertArticle" "Generated.public.articles" ]

            let testMethods =
                [ { SymbolFullName = "Tests.testListsArticles"
                    TestProject = "MyTests"
                    TestClass = "Tests"
                    TestMethod = "testListsArticles" } ]

            // The extension reads the core graph, then its edges are merged back in —
            // the same order the orchestrator uses.
            let store =
                InMemoryStore.fromAnalysisResults [ AnalysisResult.Create(symbols, coreDeps, testMethods) ]

            let sqlEdges =
                (SqlHydraExtension("Generated") :> ITestPruneExtension).AnalyzeEdges store [] ""

            db.RebuildProjects([ AnalysisResult.Create(symbols, coreDeps @ sqlEdges, testMethods) ])

            let affected = db.QueryAffectedTests([ "Repo.upsertArticle" ])
            test <@ affected |> List.map (fun t -> t.TestMethod) = [ "testListsArticles" ] @>)
