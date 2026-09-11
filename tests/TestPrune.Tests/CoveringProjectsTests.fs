module TestPrune.Tests.CoveringProjectsTests

open Xunit
open Swensen.Unquote
open Microsoft.Data.Sqlite
open TestPrune.AstAnalyzer
open TestPrune.Tests.TestHelpers

let private symbol name kind =
    { FullName = name
      Kind = kind
      SourceFile = "Graph.fs"
      LineStart = 1
      LineEnd = 1
      ContentHash = "graph"
      IsExtern = false }

let private edge source target kind =
    { FromSymbol = source
      ToSymbol = target
      Kind = kind
      Source = "extension" }

let private testMethod name project =
    { SymbolFullName = name
      TestProject = project
      TestClass = name
      TestMethod = "runs" }

let private graph =
    { Symbols =
        [ symbol "Outer" Type
          symbol "Outer.Inner" Type
          symbol "Outer.Inner.first" Function
          symbol "Outer.Inner.second" Function
          symbol "Outer.sibling" Function
          symbol "Module" Module
          symbol "Module.first" Function
          symbol "Module.second" Function
          symbol "Library.call" Function
          symbol "Library.uncovered" Function
          symbol "Library.cycle" Function
          symbol "Wiring" Function
          symbol "Unit.test" Function
          symbol "Integration.test" Function
          symbol "Sibling.test" Function
          symbol "Module.test" Function ]
      Dependencies =
        [ edge "Unit.test" "Library.call" Calls
          edge "Library.call" "Outer.Inner.first" UsesType
          edge "Integration.test" "Wiring" Calls
          edge "Wiring" "Library.call" Calls
          edge "Library.call" "Library.cycle" Calls
          edge "Library.cycle" "Library.call" Calls
          edge "Sibling.test" "Outer.sibling" Calls
          edge "Module.test" "Module.first" Calls ]
      TestMethods =
        [ testMethod "Unit.test" "Unit"
          testMethod "Integration.test" "Integration"
          testMethod "Sibling.test" "Sibling"
          testMethod "Module.test" "ModuleTests" ]
      Attributes =
        [ { SymbolFullName = "Wiring"
            AttributeName = "CompositionRootAttribute"
            ArgsJson = "[]" } ]
      ParentLinks =
        [ { Child = "Outer.Inner"; Parent = "Outer" }
          { Child = "Outer.Inner.first"; Parent = "Outer.Inner" }
          { Child = "Outer.Inner.second"; Parent = "Outer.Inner" }
          { Child = "Outer.sibling"; Parent = "Outer" }
          { Child = "Module.first"; Parent = "Module" }
          { Child = "Module.second"; Parent = "Module" } ]
      Diagnostics = AnalysisDiagnostics.Zero }

[<Fact>]
let ``bulk coverage exactly matches single-seed project sets across nested types cycles and barriers`` () =
    withDb (fun database ->
        database.RebuildProjects [ graph ]
        let seeds = "Missing" :: (graph.Symbols |> List.map _.FullName)
        let actual = database.QueryCoveringProjectsBySymbol seeds

        let expected =
            seeds
            |> List.map (fun seed ->
                seed, database.QueryAffectedTests [ seed ] |> List.map _.TestProject |> Set.ofList)
            |> Map.ofList

        test <@ actual = expected @>
        test <@ actual["Library.call"] = Set.ofList [ "Unit"; "Integration" ] @>
        test <@ actual["Outer.Inner.second"] = Set.ofList [ "Unit"; "Integration" ] @>
        test <@ actual["Outer.sibling"] = Set.singleton "Sibling" @>
        test <@ actual["Module.second"] = Set.empty @>
        test <@ actual["Unit.test"] = Set.singleton "Unit" @>
        test <@ actual["Missing"] = Set.empty @>)

[<Fact>]
let ``bulk coverage preserves requested uncovered symbols without leaking unrelated covered ones`` () =
    withDb (fun database ->
        database.RebuildProjects [ graph ]
        test <@ database.QueryCoveringProjectsBySymbol [] = Map.empty @>
        let missing = [ for index in 1..40000 -> $"Missing.%d{index}" ]
        let actual = database.QueryCoveringProjectsBySymbol missing
        test <@ actual.Count = missing.Length @>
        test <@ actual |> Map.forall (fun _ projects -> Set.isEmpty projects) @>)

[<Fact>]
let ``bulk coverage reads current graph instead of reusing a prior generation`` () =
    withDb (fun database ->
        database.RebuildProjects [ graph ]
        let before = database.QueryCoveringProjectsBySymbol [ "Library.call" ]
        database.RebuildProjects [ { graph with Dependencies = [] } ]
        let after = database.QueryCoveringProjectsBySymbol [ "Library.call" ]
        test <@ before["Library.call"] = Set.ofList [ "Unit"; "Integration" ] @>
        test <@ after["Library.call"] = Set.empty @>)

[<Fact>]
let ``bulk coverage cannot turn an unreadable graph into uncovered debt`` () =
    withDb (fun database ->
        database.RebuildProjects [ graph ]
        use connection = database.OpenConnection()
        use command = connection.CreateCommand()
        command.CommandText <- "DROP TABLE test_methods"
        command.ExecuteNonQuery() |> ignore
        Assert.Throws<SqliteException>(fun () -> database.QueryCoveringProjectsBySymbol [ "Library.call" ] |> ignore)
        |> ignore)
