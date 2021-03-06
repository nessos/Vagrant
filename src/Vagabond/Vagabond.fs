﻿namespace MBrace.Vagabond
    
open System
open System.IO
open System.Reflection

open MBrace.FsPickler
open MBrace.FsPickler.Hashing

open MBrace.Vagabond.Utils
open MBrace.Vagabond.AssemblyNaming
open MBrace.Vagabond.DependencyAnalysis
open MBrace.Vagabond.Control

type HashResult = MBrace.FsPickler.Hashing.HashResult

/// Vagabond management object which instantiates a dynamic assembly compiler, loader and exporter states
[<AutoSerializable(false)>]
type VagabondManager internal (config : VagabondConfiguration) =
    static do registerAssemblyResolutionHandler ()

    let uuid = Guid.NewGuid()
    let controller = new VagabondController(uuid, config)
    do controller.Start()

    static let ensureDynamic (a : Assembly) = 
        match a with
        | StaticAssembly _ -> invalidArg a.FullName "Vagabond: not a dynamic assembly"
        | _ -> ()

    static let toArray (ts : seq<'T>) = ts |> Seq.distinct |> Seq.toArray

    let compile (assemblies : Assembly []) = 
        controller.PostAndReply(fun ch -> CompileAssemblies(assemblies, ch))

    /// Unique identifier for the slice compiler
    member __.UUId = controller.CompilerState.CompilerId
    /// Gets the serializer instance used by the slice compiler
    member __.Serializer = controller.Serializer
    /// FsPickler type name converter for use with other formats
    member __.TypeConverter = controller.TypeNameConverter
    /// Cache directory used by Vagabond
    member __.CachePath = controller.CacheDirectory
    /// Gets or sets the default load policy for the instance
    member __.DefaultLoadPolicy = config.AssemblyLookupPolicy
    /// Vagabond Configuration record
    member __.Configuration = config

    /// <summary>
    ///     Includes an unmanaged assembly dependency to the vagabond instance.
    /// </summary>
    /// <param name="path">Path to the unmanaged assembly.</param>
    /// <param name="name">Identifier for unmanaged assembly. Defaults to the assembly file name.</param>
    [<CompilerMessage("Native assembly support is an experimental feature of Vagabond.", 1571)>]
    member __.RegisterNativeDependency(path : string) : VagabondAssembly =
        if runsOnMono.Value then raise <| new PlatformNotSupportedException("Native dependencies not supported on mono.")
        let va = VagabondAssembly.FromNativeAssembly(path)
        controller.PostAndReply(fun ch -> RegisterNativeDependency(va, ch))
        va

    /// Returns all unmanaged dependencies registered to Vagabond instance.
    member __.NativeDependencies : VagabondAssembly [] = controller.NativeDependencies


    /// Gets hash information for all Vagabond managed static bindings in current AppDomain.
    member __.StaticBindings : (FieldInfo * HashResult) [] = controller.StaticBindings

    /// <summary>
    ///     Try get a Vagabond static bindings that matches provided object hash.
    /// </summary>
    /// <param name="hash">Input object hash.</param>
    member __.TryGetBindingByHash(hash : HashResult) : FieldInfo option =
        match controller.StaticBindings |> Array.tryFind (fun (_,h) -> h = hash) with
        | None -> None
        | Some(f,_) -> Some f

    //
    //  #region Assembly compilation
    //

    /// <summary>
    ///     Checks if assembly id is a locally generated dynamic assembly slice.
    /// </summary>
    /// <param name="id">input assembly id.</param>
    member __.IsLocalDynamicAssemblySlice(id : AssemblyId) : bool =
        controller.CompilerState.IsLocalDynamicAssemblySlice id

    /// <summary>
    ///     Compiles a new slice for given dynamic assembly, if required.
    /// </summary>
    /// <param name="assembly">Dynamic assembly to be compiled.</param>
    member __.CompileDynamicAssemblySlice (assembly : Assembly) : VagabondAssembly [] =
        do ensureDynamic assembly
        let compiled = compile [|assembly|]
        __.GetVagabondAssemblies(compiled |> Array.map (fun a -> a.AssemblyId))

    /// <summary>
    ///     Returns a collection of all assemblies that the given object depends on.
    ///     Dynamic assemblies are substituted for their corresponding static slices.
    /// </summary>
    /// <param name="obj">Serializable object graph to be traversed for dependencies.</param>
    /// <param name="permitCompilation">Compile new slices as required. Defaults to true.</param>
    /// <param name="includeNativeDependencies">Include native dependencies to set. Defaults to false.</param>
    member __.ComputeObjectDependencies(graph : obj, ?permitCompilation : bool, ?includeNativeDependencies : bool) : VagabondAssembly [] =
        let permitCompilation = defaultArg permitCompilation true
        let includeNativeDependencies = defaultArg includeNativeDependencies false

        let dependencies = gatherObjectDependencies config.IsIgnoredAssembly config.AssemblyLookupPolicy (Some controller.CompilerState) graph

        if permitCompilation then
            let assemblies = getAssemblyDependenciesRequiringCompilation controller.CompilerState dependencies
            let _ = compile assemblies in ()

        let assemblies = remapDependencies config.IsIgnoredAssembly config.AssemblyLookupPolicy controller.CompilerState dependencies
        let vagabondAssemblies = __.GetVagabondAssemblies(assemblies |> Seq.map (fun a -> a.AssemblyId) |> Seq.toArray)

        if includeNativeDependencies then
            Array.append vagabondAssemblies controller.NativeDependencies
        else
            vagabondAssemblies

    //
    //  #region Vagabond Assemblies
    //

    /// <summary>
    ///     Attempt fetching a local VagabondAssembly associated with provided assembly identifier.
    /// </summary>
    /// <param name="id">assembly identifier.</param>
    /// <param name="lookupPolicy">Specifies assembly resolution policy. Defaults to strong names only.</param>
    member __.TryGetVagabondAssembly(id : AssemblyId, ?lookupPolicy : AssemblyLookupPolicy) : VagabondAssembly option =
        let lookupPolicy = defaultArg lookupPolicy config.AssemblyLookupPolicy
        controller.PostAndReply(fun ch -> TryGetVagabondAssembly(lookupPolicy, id, ch))

    /// <summary>
    ///     Fetches a local VagabondAssembly associated with provided assembly identifier.
    /// </summary>
    /// <param name="id">assembly identifier.</param>
    /// <param name="lookupPolicy">Specifies assembly resolution policy. Defaults to strong names only.</param>
    member __.GetVagabondAssembly(id : AssemblyId, ?lookupPolicy : AssemblyLookupPolicy) : VagabondAssembly =
        let lookupPolicy = defaultArg lookupPolicy config.AssemblyLookupPolicy
        match controller.PostAndReply(fun ch -> TryGetVagabondAssembly(lookupPolicy, id, ch)) with
        | Some va -> va
        | None -> raise <| new VagabondException(sprintf "Could not locate assembly '%s' in current context." id.FullName)

    /// <summary>
    ///    Returns vagabond assemblies corresponding 
    /// </summary>
    /// <param name="ids">assembly ids.</param>
    /// <param name="lookupPolicy">Specifies assembly resolution policy. Defaults to resolving strong names only.</param>
    member __.GetVagabondAssemblies(ids : seq<AssemblyId>, ?lookupPolicy : AssemblyLookupPolicy) : VagabondAssembly [] =
        ids
        |> Seq.distinct
        |> Seq.map (fun id -> __.GetVagabondAssembly(id, ?lookupPolicy = lookupPolicy))
        |> Seq.toArray

    /// <summary>
    ///     Gets the local assembly load info for given assembly id.
    /// </summary>
    /// <param name="id">Given assembly id.</param>
    /// <param name="lookupPolicy">Specifies assembly resolution policy. Defaults to resolving strong names only.</param>
    member __.GetAssemblyLoadInfo(id : AssemblyId, ?lookupPolicy : AssemblyLookupPolicy) : AssemblyLoadInfo =
        let lookupPolicy = defaultArg lookupPolicy config.AssemblyLookupPolicy
        controller.PostAndReply(fun ch -> GetAssemblyLoadInfo(lookupPolicy, id, ch))

    /// <summary>
    ///     Gets the local assembly load info for given assembly ids.
    /// </summary>
    /// <param name="ids">Given assembly ids.</param>
    /// <param name="lookupPolicy">Specifies assembly resolution policy. Defaults to resolving strong names only.</param>
    member __.GetAssemblyLoadInfo(ids : seq<AssemblyId>, ?lookupPolicy : AssemblyLookupPolicy) : AssemblyLoadInfo [] =
        ids
        |> Seq.distinct
        |> Seq.map (fun id -> __.GetAssemblyLoadInfo(id, ?lookupPolicy = lookupPolicy))
        |> Seq.toArray

    //
    //  #region Asssembly loading API
    //

    /// <summary>
    ///     Loads vagabond assembly to the local AppDomain.
    /// </summary>
    /// <param name="va">Input assembly package.</param>
    /// <param name="lookupPolicy">Specifies assembly resolution policy. Defaults to resolving strong names only.</param>
    member __.LoadVagabondAssembly(va : VagabondAssembly, ?lookupPolicy : AssemblyLookupPolicy) : AssemblyLoadInfo =
        let lookupPolicy = defaultArg lookupPolicy config.AssemblyLookupPolicy
        controller.PostAndReply(fun ch -> LoadAssembly(lookupPolicy, va, ch))

    /// <summary>
    ///     Loads vagabond assemblies to the local AppDomain.
    /// </summary>
    /// <param name="vas">Input assembly packages.</param>
    /// <param name="lookupPolicy">Specifies assembly resolution policy. Defaults to resolving strong names only.</param>
    member __.LoadVagabondAssemblies(vas : seq<VagabondAssembly>, ?lookupPolicy : AssemblyLookupPolicy) : AssemblyLoadInfo [] =
        vas
        |> Seq.map (fun va -> __.LoadVagabondAssembly(va, ?lookupPolicy = lookupPolicy))
        |> Seq.toArray

    //
    // #region Assembly upload/download API
    //

    /// <summary>
    ///     Upload provided assemblies using provided uploader implementation.
    /// </summary>
    /// <param name="uploader">Assembly uploader implementation.</param>
    /// <param name="assemblies">Assemblies to be uploaded.</param>
    member __.UploadAssemblies(uploader : IAssemblyUploader, assemblies : seq<VagabondAssembly>) : Async<unit> = async {
        return! controller.PostAndAsyncReply(fun ch -> ExportAssemblies(uploader, Seq.toArray assemblies, ch))
    }

    /// <summary>
    ///     Download provided assemblies to cache using provided downloader implementation.
    ///     Files will be stored in Vagabond cache directory but not loaded in local application domain.
    /// </summary>
    /// <param name="downloader">Assembly downloader implementation.</param>
    /// <param name="ids">Assembly ids to be downloaded.</param>
    member __.DownloadAssemblies(downloader : IAssemblyDownloader, ids : seq<AssemblyId>) : Async<VagabondAssembly []> = async {
        return! controller.PostAndAsyncReply(fun ch -> ImportAssemblies(downloader, toArray ids, ch))
    }


/// <summary>
///     A collection of general purpose utilities on dependency traversal.
/// </summary>
type Vagabond =

    /// <summary>
    ///     Initializes a Vagabond instance given supplied configuration object.
    /// </summary>
    /// <param name="config">Configuration record with which to initialize the instance.</param>
    static member Initialize(config:VagabondConfiguration) = new VagabondManager(config)

    /// <summary>
    ///     Initializes a new Vagabond instance.
    /// </summary>
    /// <param name="cacheDirectory">Temp folder used for assembly compilation and caching. Defaults to system temp folder.</param>
    /// <param name="profiles">Dynamic assembly configuration profiles.</param>
    /// <param name="typeConverter">FsPickler type name converter.</param>
    /// <param name="forceLocalFSharpCore">Force local FSharp.Core assembly when deserializing types. Defaults to false.</param>
    /// <param name="forceSerializationCompilation">Force on-demand slice compilation when required by serializer. Defaults to true.</param>
    /// <param name="isIgnoredAssembly">User-defined assembly ignore predicate.</param>
    /// <param name="lookupPolicy">Default assembly load policy.</param>
    /// <param name="dataCompressionAlgorithm">Data compression algorithm used by Vagabond. Defaults to GzipStream.</param>
    /// <param name="dataPersistThreshold">
    ///     Specifies persist threshold for data dependencies in bytes.
    ///     Objects exceeding the threshold will be persisted to files,
    ///     while all-others will be pickled in-memory. Defaults to 10 KiB.
    /// </param>
    static member Initialize(?cacheDirectory : string, ?profiles : seq<IDynamicAssemblyProfile>, ?typeConverter : ITypeNameConverter, 
                                ?forceLocalFSharpCore : bool, ?forceSerializationCompilation : bool, ?isIgnoredAssembly : Assembly -> bool, ?lookupPolicy : AssemblyLookupPolicy, 
                                    ?dataCompressionAlgorithm : ICompressionAlgorithm, ?dataPersistTreshold : int64) : VagabondManager =

        let cacheDirectory = 
            match cacheDirectory with 
            | Some d when Directory.Exists d -> d
            | Some d -> raise <| new DirectoryNotFoundException(d)
            | None -> 
                let subdir = sprintf "vagabond-%s" <| Guid.NewGuid().ToString("N")
                let path = Path.Combine(Path.GetTempPath(), subdir)
                let _ = Directory.CreateDirectory(path)
                path

        let isIgnoredAssembly = defaultArg isIgnoredAssembly (fun _ -> false)
        let forceLocalFSharpCore = defaultArg forceLocalFSharpCore false
        let forceSerializationCompilation = defaultArg forceSerializationCompilation true
        let dataPersistTreshold = defaultArg dataPersistTreshold (10L * 1024L)

        let profiles =
            match profiles with
            | Some ps -> Seq.toArray ps
            | None -> [| new FsiDynamicAssemblyProfile() :> IDynamicAssemblyProfile |]

        let lookupPolicy = defaultArg lookupPolicy (AssemblyLookupPolicy.ResolveRuntimeStrongNames ||| AssemblyLookupPolicy.ResolveVagabondCache)

        let dataCompressionAlgorithm =
            match dataCompressionAlgorithm with
            | Some a -> a
            | None -> new GzipCompression(Compression.CompressionLevel.Fastest) :> _

        let config =
            {
                CacheDirectory = cacheDirectory
                AssemblyLookupPolicy = lookupPolicy
                DataPersistThreshold = dataPersistTreshold
                DynamicAssemblyProfiles = profiles
                TypeConverter = typeConverter
                ForceLocalFSharpCoreAssembly = forceLocalFSharpCore
                IsIgnoredAssembly = isIgnoredAssembly
                DataCompressionAlgorithm = dataCompressionAlgorithm
                ForceSerializationSliceCompilation = forceSerializationCompilation
            }

        new VagabondManager(config)


    /// <summary>
    ///     Initializes a new Vagabond instance.
    /// </summary>
    /// <param name="ignoredAssemblies">Ignore assemblies and their dependencies.</param>
    /// <param name="cacheDirectory">Temp folder used for assembly compilation and caching. Defaults to system temp folder.</param>
    /// <param name="profiles">Dynamic assembly configuration profiles.</param>
    /// <param name="typeConverter">FsPickler type name converter.</param>
    /// <param name="forceLocalFSharpCore">Force local FSharp.Core assembly when deserializing types. Defaults to false.</param>
    /// <param name="forceSerializationCompilation">Force on-demand slice compilation when required by serializer. Defaults to true.</param>
    /// <param name="lookupPolicy">Default assembly load policy.</param>
    /// <param name="dataCompressionAlgorithm">Data compression algorithm used by Vagabond. Defaults to GzipStream.</param>
    /// <param name="dataPersistThreshold">
    ///     Specifies persist threshold for data dependencies in bytes.
    ///     Objects exceeding the threshold will be persisted to files,
    ///     while all-others will be pickled in-memory. Defaults to 10KiB.
    /// </param>
    static member Initialize(ignoredAssemblies : seq<Assembly>, ?cacheDirectory : string, ?profiles : seq<IDynamicAssemblyProfile>,
                                ?typeConverter : ITypeNameConverter, ?forceLocalFSharpCore : bool, ?forceSerializationCompilation : bool, ?lookupPolicy : AssemblyLookupPolicy,
                                ?dataCompressionAlgorithm : ICompressionAlgorithm, ?dataPersistTreshold : int64) : VagabondManager =
        let traversedIgnored = traverseDependencies (fun _ -> false) AssemblyLookupPolicy.None None ignoredAssemblies
        let ignoredSet = new System.Collections.Generic.HashSet<_>(traversedIgnored)
        Vagabond.Initialize(?cacheDirectory = cacheDirectory, ?profiles = profiles, isIgnoredAssembly = ignoredSet.Contains, 
                                ?typeConverter = typeConverter, ?forceLocalFSharpCore = forceLocalFSharpCore, ?forceSerializationCompilation = forceSerializationCompilation,
                                ?lookupPolicy = lookupPolicy, ?dataCompressionAlgorithm = dataCompressionAlgorithm, ?dataPersistTreshold = dataPersistTreshold)

    /// <summary>
    ///     Resolves all assembly dependencies of given object graph.
    /// </summary>
    /// <param name="obj">object graph to be traversed</param>
    /// <param name="isIgnoredAssembly">User-defined assembly ignore predicate.</param>
    /// <param name="policy">Assembly lookup policy. Defaults to require dependencies loaded in AppDomain.</param>
    static member ComputeAssemblyDependencies(obj:obj, ?isIgnoredAssembly, ?policy:AssemblyLookupPolicy) : Assembly [] =
        let policy = defaultArg policy AssemblyLookupPolicy.RequireLocalDependenciesLoadedInAppDomain
        let isIgnoredAssembly = defaultArg isIgnoredAssembly (fun _ -> false)
        gatherObjectDependencies isIgnoredAssembly policy None obj 
        |> Seq.map fst
        |> Seq.toArray

    /// <summary>
    ///     Resolves all assembly dependencies of given assembly.
    /// </summary>
    /// <param name="assembly">assembly to be traversed</param>
    /// <param name="isIgnoredAssembly">User-defined assembly ignore predicate.</param>
    /// <param name="policy">Assembly lookup policy. Defaults to require dependencies loaded in AppDomain.</param>
    static member ComputeAssemblyDependencies(assembly:Assembly, ?isIgnoredAssembly : Assembly -> bool, ?policy : AssemblyLookupPolicy) : Assembly [] = 
        let policy = defaultArg policy AssemblyLookupPolicy.RequireLocalDependenciesLoadedInAppDomain
        let isIgnoredAssembly = defaultArg isIgnoredAssembly (fun _ -> false)
        traverseDependencies isIgnoredAssembly policy None [assembly]
        |> List.toArray

    /// <summary>
    ///     Resolves all assembly dependencies of given assemblies.
    /// </summary>
    /// <param name="assemblies">Starting assembly dependencies.</param>
    /// <param name="isIgnoredAssembly">User-defined assembly ignore predicate.</param>
    /// <param name="policy">Assembly lookup policy. Defaults to require dependencies loaded in AppDomain.</param>
    static member ComputeAssemblyDependencies(assemblies:seq<Assembly>, ?isIgnoredAssembly : Assembly -> bool, ?policy : AssemblyLookupPolicy) : Assembly [] = 
        let policy = defaultArg policy AssemblyLookupPolicy.RequireLocalDependenciesLoadedInAppDomain
        let isIgnoredAssembly = defaultArg isIgnoredAssembly (fun _ -> false)
        traverseDependencies isIgnoredAssembly policy None assemblies
        |> List.toArray


    /// <summary>
    ///     Computes a unique id for given static assembly.
    /// </summary>
    /// <param name="assembly">Input static assembly.</param>
    static member ComputeAssemblyId (assembly : Assembly) : AssemblyId = 
        let _ = assembly.Location // force exception in case of dynamic assembly
        assembly.AssemblyId

    /// <summary>
    ///     Computes unique id's for a collection of static assemblies.
    /// </summary>
    /// <param name="assemblies">Input static assemblies</param>
    static member ComputeAssemblyIds (assemblies : seq<Assembly>) : AssemblyId [] =
        assemblies |> Seq.map Vagabond.ComputeAssemblyId |> Seq.toArray

    /// <summary>
    ///     Generates a unique file name that corresponds to the particular assembly id.
    ///     Useful for caching implementations.
    /// </summary>
    /// <param name="id">Input assembly identifier.</param>
    static member GetUniqueFileName(id : AssemblyId) : string = id.GetFileName()

    /// <summary>
    ///     Generates a unique file name that correspond to the particular object hash.
    ///     Useful for caching implementations.
    /// </summary>
    /// <param name="hash">Input object hash.</param>
    /// <param name="assemblyPrefix">Dynamic assembly slice id used to prefix path. Defaults to no assembly prefix.</param>
    static member GetUniqueFileName(hash : HashResult, ?assemblyPrefix : AssemblyId) : string = 
        DataDependency.CreateUniqueFileNameByHash(hash, ?prefixId = assemblyPrefix)

    /// <summary>
    ///     Attempts to parse an input assembly name that satisfied the Vagabond assembly slice format.
    ///     Returns the Vagabond uuid in the slice compiling process, the original dynamic assembly name
    ///     and the current slice index.
    /// </summary>
    /// <param name="sliceName">Slice assembly name to be parsed.</param>
    static member TryParseAssemblySliceName(sliceName : string) : (Guid * string * int) option = AssemblySliceName.tryParseDynamicAssemblySlice sliceName