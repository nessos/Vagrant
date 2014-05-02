﻿module internal Nessos.Vagrant.SliceCompilerTypes

    open System
    open System.Reflection

   // internal compiler data structures

    type DynamicTypeInfo =
        | InNoSlice
        | InAllSlices
        | InSpecificSlice of DynamicAssemblySlice

    and DynamicAssemblySlice =
        {
            SourceId : Guid
            DynamicAssemblyQualifiedName : string

            SliceId : int
            Assembly : Assembly

            StaticFields : FieldInfo []
        }
    with
        member slice.RequiresStaticInitialization = slice.StaticFields.Length > 0

    and DynamicAssemblyState =
        {
            DynamicAssembly : Assembly
            Profile : IDynamicAssemblyProfile
            GeneratedSlices : Map<int, DynamicAssemblySlice>
            TypeIndex : Map<string, DynamicTypeInfo>
        }
    with
        member s.Name = s.DynamicAssembly.GetName()

        member s.LatestSlice = s.GeneratedSlices.TryFind s.GeneratedSlices.Count

        member i.TryGetSlice(t : Type) =
            match i.TypeIndex.TryFind t.FullName with
            | None -> None
            | Some (InNoSlice | InAllSlices) -> raise <| new VagrantException(sprintf "type '%O' does not correspond to a slice." t)
            | Some (InSpecificSlice s) -> Some s

        static member Init(a : Assembly, profile : IDynamicAssemblyProfile) =
            {
                DynamicAssembly = a
                Profile = profile
                GeneratedSlices = Map.empty
                TypeIndex = Map.empty
            }

    and DynamicAssemblyCompilerState =
        {
            ServerId : Guid
            Profiles : IDynamicAssemblyProfile list
            OutputDirectory : string

            DynamicAssemblies : Map<string, DynamicAssemblyState>
            
            TryGetDynamicAssemblyId : string -> (string * int) option
            CreateAssemblySliceName : string -> int -> string
        }
    with
        member s.TryFindSliceInfo(sliceName : string) =
            match s.TryGetDynamicAssemblyId sliceName with
            | Some(dynamicAssemblyName, sliceId) ->
                let assemblyName = new AssemblyName(sliceName)
                do assemblyName.Name <- dynamicAssemblyName
                match s.DynamicAssemblies.TryFind assemblyName.FullName with
                | None -> None
                | Some info -> info.GeneratedSlices.TryFind sliceId |> Option.map (fun slice -> info, slice)
            | None -> None