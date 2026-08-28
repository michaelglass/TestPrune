namespace TestPrune.SqlHydra

open System
open System.Text.RegularExpressions
open TestPrune.AstAnalyzer
open TestPrune.Extensions
open TestPrune.Ports
open TestPrune.Sql

/// Parsed table reference from a SqlHydra generated type name.
type TableReference = { Schema: string; Table: string }

/// Analyzes SqlHydra typed symbol references to automatically produce SQL access facts.
module SqlHydraAnalyzer =

    let private selectBuilderMember =
        Regex(
            @"^SqlHydra\.Query\.SelectBuilders\.SelectBuilder(?:`\d+)?\.[A-Za-z_][A-Za-z0-9_']*(?:``\d+)?$",
            RegexOptions.Compiled
        )

    let private mutationBuilderMember =
        Regex(
            @"^SqlHydra\.Query\.(?:InsertBuilders\.InsertBuilder|UpdateBuilders\.UpdateBuilder|DeleteBuilders\.DeleteBuilder)(?:`\d+)?\.[A-Za-z_][A-Za-z0-9_']*(?:``\d+)?$",
            RegexOptions.Compiled
        )

    let private terminalHelper =
        Regex(
            @"^(?:SqlHydra\.Query\.(?:(?:SelectBuilders|InsertBuilders|UpdateBuilders|DeleteBuilders)\.)?)?(?<name>selectTask|selectAsync|select|insertTask|insertAsync|insert|updateTask|updateAsync|update|deleteTask|deleteAsync|delete)(?:``\d+)?$",
            RegexOptions.Compiled
        )

    /// Classify a SqlHydra DSL function name as Read or Write access.
    let classifyDslContext (functionName: string) : AccessKind option =
        // FCS names computation-expression custom operations after the member
        // they resolve to (for example SelectBuilder`2.Where), not after the
        // source keyword. Recognise those typed builder symbols as well as the
        // terminal helpers retained for older SqlHydra versions.
        if selectBuilderMember.IsMatch functionName then
            Some Read
        elif mutationBuilderMember.IsMatch functionName then
            Some Write
        else
            let terminal = terminalHelper.Match functionName

            match
                if terminal.Success then
                    terminal.Groups.["name"].Value
                else
                    ""
            with
            | "selectTask"
            | "selectAsync"
            | "select" -> Some Read
            | "insertTask"
            | "insertAsync"
            | "insert" -> Some Write
            | "updateTask"
            | "updateAsync"
            | "update" -> Some Write
            | "deleteTask"
            | "deleteAsync"
            | "delete" -> Some Write
            | _ -> None

    /// Parse a fully-qualified SqlHydra generated type name to extract schema and table.
    /// SqlHydra generates types like "Generated.public.briefs" or "MyDb.Generated.public.articles".
    /// We look for the last two dotted segments as schema.table. This legacy,
    /// prefix-free helper cannot distinguish a type from a member-shaped name;
    /// extension code must use `parseTableReferenceUnder` below.
    let parseTableReference (fullName: string) : TableReference option =
        let parts = fullName.Split('.')

        if parts.Length >= 3 then
            let schema = parts[parts.Length - 2]
            let table = parts[parts.Length - 1]
            Some { Schema = schema; Table = table }
        else
            None

    /// Parse a generated table type relative to an exact module boundary.
    /// Unlike `parseTableReference`, this rejects member-shaped names with more
    /// than `schema.table` after the configured generated module.
    let parseTableReferenceUnder (generatedModulePrefix: string) (fullName: string) : TableReference option =
        if String.IsNullOrWhiteSpace generatedModulePrefix then
            invalidArg (nameof generatedModulePrefix) "Generated module prefix must not be blank."

        let marker = $".%s{generatedModulePrefix}."

        let suffix =
            if fullName.StartsWith($"%s{generatedModulePrefix}.", StringComparison.Ordinal) then
                Some fullName[generatedModulePrefix.Length + 1 ..]
            else
                let i = fullName.IndexOf(marker, StringComparison.Ordinal)
                if i >= 0 then Some fullName[i + marker.Length ..] else None

        match suffix |> Option.map (fun value -> value.Split('.')) with
        | Some [| schema; table |] when schema <> "" && table <> "" -> Some { Schema = schema; Table = table }
        | _ -> None

/// Extension that detects SqlHydra query patterns in the dependency graph
/// and produces SharedState edges via SqlCoupling.
type SqlHydraExtension(generatedModulePrefix: string) =

    do
        if String.IsNullOrWhiteSpace generatedModulePrefix then
            invalidArg (nameof generatedModulePrefix) "Generated module prefix must not be blank."

    static member extractFacts (prefix: string) (store: SymbolStore) : SqlFact list =
        if String.IsNullOrWhiteSpace prefix then
            invalidArg (nameof prefix) "Generated module prefix must not be blank."

        let allSymbols = store.GetAllSymbols() |> List.filter (fun s -> not s.IsExtern)

        let depsByFile =
            allSymbols
            |> List.map (fun s -> s.SourceFile)
            |> List.distinct
            |> List.map (fun f -> f, store.GetDependenciesFromFile f)
            |> Map.ofList

        allSymbols
        |> List.collect (fun sym ->
            let deps =
                depsByFile
                |> Map.tryFind sym.SourceFile
                |> Option.defaultValue []
                |> List.filter (fun d -> d.FromSymbol = sym.FullName)

            // EVERY DSL access this symbol performs, de-duplicated — not just the first.
            //
            // A `Dependency` carries no source range and `GetDependenciesFromFile` has no
            // ORDER BY, so we cannot pair a given DSL call with the table it operates on.
            // Keeping only the FIRST access would discard the rest, with SQLite's row order
            // deciding which survived: an upsert-style symbol that selects and then inserts
            // is recorded as a pure READER, its table ends up with no writer at all, and
            // readers of that table get no edge — under-selection, the one failure mode a
            // test-impact tool must not have.
            //
            // Keeping them all is exact for the common single-access symbol and degrades to
            // a conservative (access x table) product only for a symbol that genuinely mixes
            // reads and writes — where no finer answer is derivable from the data we have.
            // It can only ever ADD edges, so it cannot drop a genuinely-affected test.
            let dslAccesses =
                deps
                |> List.choose (fun d ->
                    if d.Kind = Calls then
                        SqlHydraAnalyzer.classifyDslContext d.ToSymbol
                    else
                        None)
                |> List.distinct

            let tableRefs =
                deps
                |> List.choose (fun d ->
                    if d.Kind = UsesType then
                        SqlHydraAnalyzer.parseTableReferenceUnder prefix d.ToSymbol
                    else
                        None)
                |> List.distinct

            [ for tref in tableRefs do
                  for access in dslAccesses do
                      { Symbol = sym.FullName
                        Table = tref.Table
                        Column = "*"
                        Access = access } ])

    interface ITestPruneExtension with
        member _.Name = "SqlHydra"

        member _.AnalyzeEdges (symbolStore: SymbolStore) (_changedFiles: string list) (_repoRoot: string) =
            SqlHydraExtension.extractFacts generatedModulePrefix symbolStore
            |> SqlCoupling.buildEdges
            |> List.map (fun d -> { d with Source = "sql-hydra" })
