namespace SqlClientTypeProvider

open System

// Put the TypeProviderAssemblyAttribute in the runtime DLL, pointing to the design-time DLL
[<assembly:CompilerServices.TypeProviderAssembly("SqlClientTypeProvider.DesignTime.dll")>]
do ()