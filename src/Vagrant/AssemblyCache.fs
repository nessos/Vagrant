﻿module internal Nessos.Vagrant.AssemblyCache

    open System
    open System.IO
    open System.Reflection

    open Nessos.FsPickler

    open Nessos.Vagrant
    open Nessos.Vagrant.Utils
    open Nessos.Vagrant.AssemblyLoader

    type AssemblyCache = StatefulActor<Map<AssemblyId, bool * AssemblyLoadInfo>, PortableAssembly * bool * AssemblyLocalResolutionPolicy, AssemblyLoadInfo>

    type CachePath =
        {
            Assembly : string
            Pdb : string
            StaticInitializer : string
            Metadata : string
        }
    with
        /// resolve unique paths for provided assembly in given cache directory
        static member Resolve (cacheDir : string) (id : AssemblyId) =
            let hash = Convert.toBase32String id.ImageHash
            let name = sprintf "%s-%s" (id.GetName().Name) hash
            let basePath = Path.Combine(cacheDir, name)
            {
                Assembly = basePath + ".dll"
                Pdb = basePath + ".pdb"
                StaticInitializer = basePath + ".init"
                Metadata = basePath + ".vagrant"
            }

    /// write dynamic assembly metadata to cache
    let writeMetadata (pickler : BasePickler) (path : CachePath) info =
        use fs = new FileStream(path.Metadata, FileMode.Create)
        pickler.Serialize<StaticInitializationInfo>(fs, info)

    /// read dynamic assembly metadata from cache
    let readMetadata (pickler : BasePickler) (path : CachePath) =
        use fs = new FileStream(path.Metadata, FileMode.Open)
        pickler.Deserialize<StaticInitializationInfo>(fs)

    /// query cache dir for current state
    let resolveCachedAssemblyInfo (pickler : BasePickler) (path : CachePath) requireIdenticalAssembly assemblyLoadPolicy (id : AssemblyId) =

        let tryLoadLocal =
            match assemblyLoadPolicy with
            | AssemblyLocalResolutionPolicy.All -> true
            | AssemblyLocalResolutionPolicy.StrongNamesOnly when id.IsStrongAssembly -> true
            | _ -> false

        let isLoadedLocal =
            if tryLoadLocal then
                tryLoadAssembly id.FullName |> Option.isSome
            else
                match tryGetLoadedAssembly id.FullName with
                | Some a when requireIdenticalAssembly && a.AssemblyId = id -> true
                | _ -> false

        if not (isLoadedLocal || File.Exists path.Assembly) then false, NotLoaded id
        elif File.Exists path.Metadata then
            let info = readMetadata pickler path
            if File.Exists path.StaticInitializer then
                isLoadedLocal, LoadedWithStaticIntialization (id, info)
            else
                let msg = sprintf "cache error: missing static initialization file for assembly '%s'" id.FullName
                false, LoadFault(id, VagrantException(msg))
        else
            isLoadedLocal, Loaded id

    /// write new static initializer to cache
    let writeStaticInitializer (pickler : BasePickler) (path : CachePath) (previous : StaticInitializationInfo option) (init : StaticInitializer) =
        match previous with
        | Some p when p.Generation > init.Generation -> p
        | _ ->
            let info = { Generation = init.Generation ; Errors = [||] ; IsPartial = init.IsPartial }
            writeMetadata pickler path info
            File.WriteAllBytes(path.StaticInitializer, init.Data)
            info

    /// write portable assembly to cache
    let writeAssemblyToCache (pickler : BasePickler) (path : CachePath) (pa : PortableAssembly) =
        match pa.Image with
        | None -> NotLoaded pa.Id
        | Some img -> 

            do File.WriteAllBytes(path.Assembly, img)

            match pa.Symbols with
            | None -> ()
            | Some symbols -> File.WriteAllBytes(path.Pdb, symbols)

            // cache the static initializer
            match pa.StaticInitializer with
            | None -> Loaded pa.Id
            | Some init -> 
                let info = writeStaticInitializer pickler path None init
                LoadedWithStaticIntialization(pa.Id, info)

    /// the main portable assembly method
    let cachePortableAssembly (pickler : BasePickler) (cacheDir : string) 
                                (state : Map<AssemblyId, bool * AssemblyLoadInfo>) 
                                requireIdentical loadPolicy (pa : PortableAssembly) =

        try
            let path = CachePath.Resolve cacheDir pa.Id

            let isLoadedInAppDomain, isFirstAccess, loadState =
                match state.TryFind pa.Id with
                | None -> 
                    let isAppDomain, loadState = resolveCachedAssemblyInfo pickler path requireIdentical loadPolicy pa.Id
                    isAppDomain, true, loadState
                | Some (isAppDomain, loadState) -> isAppDomain, false, loadState

            let success info = state.Add(pa.Id, (isLoadedInAppDomain, info)), info

            match loadState, pa.StaticInitializer with
            | NotLoaded _, _ -> success <| writeAssemblyToCache pickler path pa
            | LoadedWithStaticIntialization(_,info), Some init -> 
                let info = writeStaticInitializer pickler path (Some info) init
                success <| LoadedWithStaticIntialization(pa.Id, info)

            | _ when isFirstAccess -> success loadState
            | _ -> state, loadState
        
        with e -> state, LoadFault(pa.Id, e)



    let initAssemblyCache (pickler : BasePickler) (cacheDir : string) : AssemblyCache =
        mkStatefulActor Map.empty (fun state (pa,req,policy) -> cachePortableAssembly pickler cacheDir state req policy pa)


    /// load a portable assembly from cache
    let tryGetPortableAssemblyFromCache (cache : AssemblyCache) cacheDir includeImage requireIdentical policy (id : AssemblyId) =
        match cache.PostAndReply (PortableAssembly.Empty id, requireIdentical, policy) with
        | LoadFault(_,e) -> raise e
        | NotLoaded _ -> None
        | loadState ->
            // check if assembly is recorded as loaded in AppDomain
            let isLoadedInAppDomain = defaultArg (cache.CurrentState.TryFind id |> Option.map fst) false

            let path = CachePath.Resolve cacheDir id
            let image, symbols =
                if includeImage then
                    let dll = 
                        if isLoadedInAppDomain then tryLoadAssembly(id.FullName).Value.Location
                        else path.Assembly

                    let image = Some <| File.ReadAllBytes dll
                       
                    let pdb = 
                        if isLoadedInAppDomain then Path.ChangeExtension(dll, ".pdb")
                        else path.Pdb

                    let symbols =
                        if File.Exists pdb then
                            Some <| File.ReadAllBytes pdb
                        else
                            None

                    image, symbols
                else
                    None, None

            let staticInit =
                match loadState with
                | LoadedWithStaticIntialization (_,info) ->
                    let data = File.ReadAllBytes path.StaticInitializer
                    Some { Data = data ; IsPartial = info.IsPartial ; Generation = info.Generation }

                | _ -> None

            Some { Id = id ; Image = image ; Symbols = symbols ; StaticInitializer = staticInit }