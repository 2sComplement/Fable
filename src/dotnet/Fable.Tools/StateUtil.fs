module Fable.Tools.StateUtil

open Fable
open Fable.AST
open Fable.State
open System
open System.Reflection
open System.Collections.Concurrent
open System.Collections.Generic
open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.SourceCodeServices
open Newtonsoft.Json
open ProjectCracker

let loadAssembly path =
#if NETFX
    Assembly.LoadFrom(path)
#else
    let globalLoadContext = System.Runtime.Loader.AssemblyLoadContext.Default
    globalLoadContext.LoadFromAssemblyPath(path)
#endif

let loadPlugins pluginPaths (loadedPlugins: PluginInfo list) =
    let loadedPlugins =
        loadedPlugins |> Seq.groupBy (fun p -> p.path) |> Map
    pluginPaths
    |> Seq.collect (fun path ->
        let path = Path.normalizeFullPath path
        match Map.tryFind path loadedPlugins with
        | Some pluginInfos -> pluginInfos
        | None ->
            try
                let assembly = loadAssembly path
                assembly.GetTypes()
                |> Seq.filter typeof<IPlugin>.IsAssignableFrom
                |> Seq.map (fun x ->
                    { path = path
                    ; plugin = Activator.CreateInstance x :?> IPlugin })
            with
            | ex -> failwithf "Cannot load plugin %s: %s" path ex.Message)
    |> Seq.toList

let getRelativePath path =
    Path.getRelativePath (IO.Directory.GetCurrentDirectory()) path

let parseFSharpProject (checker: FSharpChecker) (projOptions: FSharpProjectOptions) (com: ICompiler) =
    sprintf "Parsing %s..." (getRelativePath projOptions.ProjectFileName)
    |> Log.logAlways
    let checkProjectResults =
        projOptions
        |> checker.ParseAndCheckProject
        |> Async.RunSynchronously
    for er in checkProjectResults.Errors do
        let severity =
            match er.Severity with
            | FSharpErrorSeverity.Warning -> Severity.Warning
            | FSharpErrorSeverity.Error -> Severity.Error
        let range =
            { start={ line=er.StartLineAlternate; column=er.StartColumn}
            ; ``end``={ line=er.EndLineAlternate; column=er.EndColumn} }
        com.AddLog(er.Message, severity, range, er.FileName, "FSHARP")
    checkProjectResults

let hasFlag flagName (opts: IDictionary<string, string>) =
    match opts.TryGetValue(flagName) with
    | true, value ->
        match bool.TryParse(value) with
        | true, value -> value
        | _ -> false
    | _ -> false

let tryGetOption name (opts: IDictionary<string, string>) =
    match opts.TryGetValue(name) with
    | true, value -> Some value
    | _ -> None

let createProject checker (projInfo: ProjectInfo option)
                  (com: ICompiler) (msg: Parser.Message) projFile =
    let projectOptions =
        projInfo |> Option.bind (fun i -> i.ProjectOptions) |> function
        | Some projectOptions -> projectOptions
        | None ->
            let projectOptions = getFullProjectOpts checker msg.define projFile
            if projFile.EndsWith(".fsproj") then
                Log.logVerbose("F# PROJECT: " + projectOptions.ProjectFileName)
                for option in projectOptions.OtherOptions do
                     Log.logVerbose("   " + option)
                for file in projectOptions.ProjectFileNames do
                    Log.logVerbose("   " + file)
            projectOptions
    let checkedProject = parseFSharpProject checker projectOptions com
    tryGetOption "saveAst" msg.extra |> Option.iter (fun dir ->
        Printers.printAst dir checkedProject)
    let isWatch, fableCoreJsDir =
        match projInfo with
        // If we have info from previous project, assume it's a watch compilation
        | Some info -> true, info.FableCoreJsDir
        | None -> false, ProjectCracker.tryGetFableCoreJsDir projectOptions.ProjectFileName
    Project(projectOptions, checkedProject, isWatch, ?fableCoreJsDir=fableCoreJsDir)

let toJson =
    let jsonSettings =
        JsonSerializerSettings(
            Converters=[|Json.ErasedUnionConverter()|],
            NullValueHandling=NullValueHandling.Ignore)
            // StringEscapeHandling=StringEscapeHandling.EscapeNonAscii)
    fun (value: obj) ->
        JsonConvert.SerializeObject(value, jsonSettings)

let sendError replyChannel (ex: Exception) =
    let rec innerStack (ex: Exception) =
        if isNull ex.InnerException then ex.StackTrace else innerStack ex.InnerException
    let stack = innerStack ex
    Log.logAlways(sprintf "ERROR: %s\n%s" ex.Message stack)
    ["error", ex.Message] |> dict |> toJson |> replyChannel

let addLogs (com: Compiler) (file: Babel.Program) =
    let loc = defaultArg file.loc SourceLocation.Empty
    Babel.Program(file.fileName, loc, file.body, file.directives, com.Logs, file.dependencies)

let getExtension (fileName: string) =
    let i = fileName.LastIndexOf(".")
    fileName.Substring(i).ToLower()

let updateState (checker: FSharpChecker) (com: Compiler) (state: State) (msg: Parser.Message): State * Project =
    let tryFindAndUpdateProject ext sourceFile =
        state |> Map.tryPick (fun _ project ->
            match Map.tryFind sourceFile project.FileInfos with
            | Some fileInfo ->
                // sprintf "File %s (compiled %b) belongs to project %s"
                //     (getRelativePath sourceFile) fileInfo.IsCompiled (getRelativePath project.ProjectFile)
                // |> Log.logVerbose
                let project =
                    // When a script is modified, restart the project with new options
                    // (to check for new references, loaded projects, etc.)
                    if ext = ".fsx" then
                        let projInfo = ProjectInfo(project.FableCoreJsDir) |> Some
                        createProject checker projInfo com msg project.ProjectFile
                    // Watch compilation of an .fs file, restart project with old options
                    elif fileInfo.IsCompiled then
                        let projInfo = ProjectInfo(project.FableCoreJsDir, project.ProjectOptions) |> Some
                        createProject checker projInfo com msg project.ProjectFile
                    else project
                // Set file as already compiled
                project.FileInfos.[sourceFile].IsCompiled <- true
                Some project
            | None -> None)
    let addOrUpdateProject state (project: Project) =
        let state = Map.add project.ProjectFile project state
        state, project
    let fileName = msg.path
    match getExtension fileName with
    | ".fsproj" ->
        createProject checker None com msg fileName
        |> addOrUpdateProject state
    | ".fsx" as ext ->
        match Map.tryFind fileName state with
        | Some project ->
            let projInfo = ProjectInfo(project.FableCoreJsDir) |> Some
            createProject checker projInfo com msg fileName
            |> addOrUpdateProject state
        | None ->
            match tryFindAndUpdateProject ext fileName with
            | Some project -> addOrUpdateProject state project
            | None ->
                createProject checker None com msg fileName
                |> addOrUpdateProject state
    | ".fs" as ext ->
        match tryFindAndUpdateProject ext fileName with
        | Some project -> addOrUpdateProject state project
        | None ->
            state |> Map.map (fun _ p -> p.ProjectFile) |> Seq.toList
            |> failwithf "%s doesn't belong to any of loaded projects %A" fileName
    | ".fsi" -> failwithf "Signature files cannot be compiled to JS: %s" fileName
    | _ -> failwithf "Not an F# source file: %s" fileName

let resolveFableCoreLocation (com: Compiler) (project: Project) =
    // Resolve the fable-core location if not defined by user
    if com.Options.fableCore = "fable-core" then
        match project.FableCoreJsDir with
        | Some fableCoreJsDir ->
            let newOpts = { com.Options with fableCore = Parser.makePathRelative fableCoreJsDir }
            Compiler(newOpts, com.Plugins, com.Logs)
        | None ->
            failwith "Cannot find fable-core directory"
    else com

let compile (com: Compiler) (project: Project) (fileName: string) =
    if fileName.EndsWith(".fsproj") then
        // If there are errors at this stage they must come from the F# compiler
        if com.Logs.ContainsKey("error") then
            // Don't keep compiling because the project file would remain
            // errored forever, but add all project files as dependencies.
            let deps = Array.toList project.ProjectOptions.ProjectFileNames
            Babel.Program(fileName, SourceLocation.Empty, [], dependencies=deps)
        else
            let lastFile = Array.last project.ProjectOptions.ProjectFileNames
            Fable2Babel.Compiler.createFacade fileName lastFile
        |> addLogs com |> toJson
    else
        let com = resolveFableCoreLocation com project
        FSharp2Fable.Compiler.transformFile com project project.CheckedProject fileName
        |> Fable2Babel.Compiler.transformFile com project
        |> addLogs com |> toJson

type Command = string * (string -> unit)

let startAgent () = MailboxProcessor<Command>.Start(fun agent ->
    let rec loop (checker: FSharpChecker) (com: Compiler) (state: State) = async {
        let! msg, replyChannel = agent.Receive()
        try
            let msg = Parser.parse msg
            let com = Compiler(msg.options, loadPlugins msg.plugins (com :> ICompiler).Plugins)
            let state, activeProject = updateState checker com state msg
            async {
                try
                    compile com activeProject msg.path |> replyChannel
                with ex ->
                    sendError replyChannel ex
            } |> Async.Start
            return! loop checker com state
        with ex ->
            sendError replyChannel ex
            return! loop checker com state
    }
    let compiler = Compiler()
    let checker = FSharpChecker.Create(keepAssemblyContents=true, msbuildEnabled=false)
    loop checker compiler Map.empty
  )
