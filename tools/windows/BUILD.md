# Building wavscan.exe

Built with Mono's `mcs` against the .NET Framework 2.0 reference assemblies from the `Microsoft.NETFramework.ReferenceAssemblies.net20` NuGet package, as x86:

```
mcs -nostdlib -noconfig -r:<ref>/mscorlib.dll -r:<ref>/System.dll -platform:x86 -target:exe -optimize+ -out:wavscan.exe WavScan.cs
```

where `<ref>` is `build/.NETFramework/v2.0` from the unpacked package. With Visual Studio, any project targeting .NET Framework 2.0 with "Platform target" set to "x86" builds it too.
