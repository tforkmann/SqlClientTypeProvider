namespace SqlClientTypeProvider

#if !IS_DESIGNTIME
// Put the TypeProviderAssemblyAttribute in the runtime DLL, pointing to the design-time DLL
[<assembly:CompilerServices.TypeProviderAssembly("SqlClientTypeProvider.DesignTime.dll")>]
do ()
#endif