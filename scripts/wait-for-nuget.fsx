open System
open System.Diagnostics
open System.IO
open System.Security
open System.Threading
open System.Xml.Linq

type ProbeConfig =
    { Attempts: int
      DelayMs: int
      ProcessTimeoutMs: int
      DotnetExecutable: string
      ProbeParent: string }

type RestoreResult =
    | Restored
    | RestoreFailed of detail: string
    | RestoreTimedOut of detail: string

let positiveSetting name fallback =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> Ok fallback
    | raw ->
        match Int32.TryParse raw with
        | true, value when value > 0 -> Ok value
        | _ -> Error $"%s{name} must be a positive integer, got '%s{raw}'"

let configuration () =
    match
        positiveSetting "TESTPRUNE_NUGET_PROBE_ATTEMPTS" 20,
        positiveSetting "TESTPRUNE_NUGET_PROBE_DELAY_MS" 15000,
        positiveSetting "TESTPRUNE_NUGET_PROBE_PROCESS_TIMEOUT_MS" 120000
    with
    | Ok attempts, Ok delayMs, Ok processTimeoutMs ->
        let executable =
            match Environment.GetEnvironmentVariable "TESTPRUNE_NUGET_PROBE_DOTNET" with
            | null
            | "" -> "dotnet"
            | value -> value

        let parent =
            match Environment.GetEnvironmentVariable "TESTPRUNE_NUGET_PROBE_PARENT" with
            | null
            | "" -> Path.GetTempPath()
            | value -> Path.GetFullPath value

        Ok
            { Attempts = attempts
              DelayMs = delayMs
              ProcessTimeoutMs = processTimeoutMs
              DotnetExecutable = executable
              ProbeParent = parent }
    | Error error, _, _
    | _, Error error, _
    | _, _, Error error -> Error error

let singleProjectValue (project: XDocument) projectPath elementName =
    match
        project.Descendants(XName.Get elementName)
        |> Seq.map _.Value
        |> Seq.filter (String.IsNullOrWhiteSpace >> not)
        |> Seq.distinct
        |> Seq.toList
    with
    | [ value ] -> Ok value
    | [] -> Error $"%s{projectPath} has no readable <%s{elementName}>"
    | values -> Error $"%s{projectPath} has ambiguous <%s{elementName}> values: %s{String.concat ", " values}"

let processOutput (task: Threading.Tasks.Task<string>) =
    if task.Wait(TimeSpan.FromSeconds 5.) then task.Result.Trim() else "output drain did not finish within 5s"

let runDotnet config probePackages arguments =
    let start = ProcessStartInfo(config.DotnetExecutable)
    start.UseShellExecute <- false
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.Environment["NUGET_PACKAGES"] <- probePackages

    arguments |> List.iter start.ArgumentList.Add

    try
        use child = Process.Start start
        let stdout = child.StandardOutput.ReadToEndAsync()
        let stderr = child.StandardError.ReadToEndAsync()

        if child.WaitForExit(config.ProcessTimeoutMs) then
            let detail =
                [ processOutput stdout; processOutput stderr ]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat " | "

            if child.ExitCode = 0 then Restored else RestoreFailed detail
        else
            let killDetail =
                try
                    child.Kill(true)
                    if child.WaitForExit(5000) then "process tree killed" else "process tree kill did not exit within 5s"
                with ex ->
                    $"process-tree kill failed: %s{ex.Message}"

            let detail =
                [ killDetail; processOutput stdout; processOutput stderr ]
                |> List.filter (String.IsNullOrWhiteSpace >> not)
                |> String.concat " | "

            RestoreTimedOut detail
    with ex ->
        RestoreFailed $"could not start restore process: %s{ex.Message}"

let runRestore config probeProject probeConfig probePackages =
    runDotnet
        config
        probePackages
        [ "restore"
          probeProject
          "--configfile"
          probeConfig
          "--packages"
          probePackages
          "--no-cache"
          "--force"
          "--verbosity"
          "quiet" ]

let runToolInstall config packageId version probeConfig probePackages probeTools =
    runDotnet
        config
        probePackages
        [ "tool"
          "install"
          packageId
          "--version"
          version
          "--tool-path"
          probeTools
          "--configfile"
          probeConfig
          "--no-cache"
          "--verbosity"
          "quiet" ]

let writeProbeFiles packageId version probeProject probeConfig =
    let escapedPackage = SecurityElement.Escape packageId
    let escapedVersion = SecurityElement.Escape version

    File.WriteAllText(
        probeConfig,
        """<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear />
<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
</packageSources></configuration>
"""
    )

    File.WriteAllText(
        probeProject,
        $"""<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
<ItemGroup><PackageReference Include="%s{escapedPackage}" Version="[%s{escapedVersion}]" /></ItemGroup>
</Project>
"""
    )

let probe config packageId projectPath =
    if not (File.Exists projectPath) then
        Error $"project does not exist: %s{projectPath}"
    else
        try
            let project = XDocument.Load projectPath

            match singleProjectValue project projectPath "PackageId", singleProjectValue project projectPath "Version" with
            | Error error, _
            | _, Error error -> Error error
            | Ok declaredPackageId, Ok _ when not (String.Equals(packageId, declaredPackageId, StringComparison.Ordinal)) ->
                Error $"requested %s{packageId}, but %s{projectPath} declares PackageId %s{declaredPackageId}"
            | Ok _, Ok version ->
                let isTool =
                    project.Descendants(XName.Get "PackAsTool")
                    |> Seq.exists (fun element -> String.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase))

                Directory.CreateDirectory config.ProbeParent |> ignore
                let probeId = Guid.NewGuid().ToString("N")
                let probeRoot = Path.Combine(config.ProbeParent, $"testprune-nuget-probe-%s{probeId}")
                let probeProject = Path.Combine(probeRoot, "probe.csproj")
                let probeConfig = Path.Combine(probeRoot, "NuGet.Config")
                let probePackages = Path.Combine(probeRoot, "packages")
                let probeTools = Path.Combine(probeRoot, "tools")
                Directory.CreateDirectory probeRoot |> ignore

                try
                    writeProbeFiles packageId version probeProject probeConfig

                    let rec wait attempt lastDetail =
                        if attempt > config.Attempts then
                            Error
                                $"%s{packageId} %s{version} was still not restorable from nuget.org after %d{config.Attempts} attempts. Last restore result: %s{lastDetail}"
                        else
                            let attemptResult =
                                if isTool then
                                    runToolInstall config packageId version probeConfig probePackages probeTools
                                else
                                    runRestore config probeProject probeConfig probePackages

                            match attemptResult with
                            | Restored -> Ok $"NuGet publication barrier: %s{packageId} %s{version} is restorable from nuget.org"
                            | (RestoreFailed detail | RestoreTimedOut detail) as outcome ->
                                let timedOut =
                                    match outcome with
                                    | RestoreTimedOut _ -> true
                                    | _ -> false

                                if attempt < config.Attempts then
                                    printfn
                                        "NuGet publication barrier: %s %s %s (attempt %d/%d); retrying in %dms"
                                        packageId
                                        version
                                        (if timedOut then "restore timed out" else "unavailable")
                                        attempt
                                        config.Attempts
                                        config.DelayMs

                                    Thread.Sleep config.DelayMs

                                let reason = if timedOut then "restore timed out" else "restore failed"
                                wait (attempt + 1) ($"%s{reason}: %s{detail}")

                    wait 1 "no restore attempted"
                finally
                    if Directory.Exists probeRoot then
                        Directory.Delete(probeRoot, true)
        with ex ->
            Error $"could not read release project %s{projectPath}: %s{ex.Message}"

let result =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| packageId; projectPath |] ->
        match configuration () with
        | Error error -> Error error
        | Ok config -> probe config packageId (Path.GetFullPath projectPath)
    | _ -> Error "usage: dotnet fsi scripts/wait-for-nuget.fsx -- <package-id> <project.fsproj>"

match result with
| Ok message -> printfn "%s" message
| Error error ->
    eprintfn "NuGet publication barrier failed: %s" error
    Environment.ExitCode <- 1
