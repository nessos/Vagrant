﻿namespace System
open System.Reflection

[<assembly: AssemblyVersionAttribute("0.14.0")>]
[<assembly: AssemblyFileVersionAttribute("0.14.0")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.14.0"
    let [<Literal>] InformationalVersion = "0.14.0"
