# Windows application manifests

Use the standard SDK property to embed an application manifest:

```xml
<Project Sdk="DotKt.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <ApplicationManifest>manifest files/app.manifest</ApplicationManifest>
  </PropertyGroup>
</Project>
```

The path is project-relative (absolute paths and spaces also work). DotKt passes SDK-selected inputs
to Roslyn's public resource-generation API, including the resolved `Win32Manifest` bytes as `RT_MANIFEST`.
No placeholder C# assembly is compiled. The final declarations and method bodies
are emitted from CIR. The standard SDK apphost
then carries it into the final Windows executable for build and publish. Executable outputs use resource ID 1;
Library outputs use DLL isolation-aware resource ID 2, matching C#. No managed `EmbeddedResource`,
sidecar copy or post-build patch is needed. Changing the file, switching it or clearing the property is an
incremental emission input; unchanged builds do not re-emit, and manifest-only changes do not recompile Kotlin.

The final-executable behavior applies to normal `Exe`/`WinExe` managed applications with a Windows PE apphost. `UseAppHost=false`
leaves the resource in the managed DLL only; launching that DLL with `dotnet` does not change the manifest
of the `dotnet` host process. A non-Windows apphost cannot use a Windows manifest. NativeAOT and ClickOnce
deployment manifest generation are not added by this feature. Missing or unreadable declared files
fail the build. Like the C# compiler,
the emitter transports the supplied bytes rather than validating the Windows manifest schema or OS policy.

## Defaults and opt-outs

With no selected manifest, executable projects (`Exe`/`WinExe`) receive the selected .NET SDK's default
`asInvoker` manifest. Library projects do not receive a default application manifest. Set
`<NoWin32Manifest>true</NoWin32Manifest>` to disable the default, for example when providing it in a later
build step. Explicit manifest and resource options follow the underlying SDK/compiler's selection and
conflict rules; DotKt does not overlay another manifest on that result. In particular, `asInvoker` is an
execution-policy declaration, not an unconditional resource to add to every DLL.

Native `VERSIONINFO` is also preserved, including SDK-selected file/product version and product/company
strings. This is the Windows Explorer version-information surface, distinct from CLR assembly identity
and managed attributes. The SDK also preserves the concrete CLR assembly version and the generated
`AssemblyFileVersion` / `AssemblyInformationalVersion` attributes in the Kotlin DLL. `Version`,
`VersionPrefix`/`VersionSuffix`, explicit version overrides, `SourceRevisionId` composition and
`GenerateAssemblyInfo` / individual version-attribute opt-outs follow the underlying .NET SDK.
Version-only changes refresh emission without recompiling Kotlin; unchanged builds do not re-emit.
MSBuild transports evaluated `AssemblyAttribute` string values in an intermediate JSON options file.
The emitter encodes the version identity and attributes directly; assembly-version wildcards are expanded
once during emission preparation and require `Deterministic=false`. Versions are never guessed from native strings.
MSBuild property expressions remain supported because MSBuild evaluates them before transport. Source-code
fragments supplied through `_Parameter1_IsLiteral=true` are not supported: the resource API receives
evaluated string values, not arbitrary C# attribute expressions. Kotlin source-level assembly
annotation syntax is not introduced by this build change.

`ApplicationIcon` is encoded as native icon/group resources. An explicit `Win32Resource` (RES or COFF)
replaces generated resources, rather than being merged with a default manifest or VERSIONINFO.
Switching or clearing native resource options refreshes the final PE without recompiling Kotlin.
Direct `ilemit` callers have no implicit resources: the project driver supplies `--assembly-options <JSON>`
when needed. Its values are physical build inputs, not Kotlin semantics or a previous application's metadata.
The selected SDK's Roslyn API owns VERSIONINFO, default manifests and icon conversion; AsmResolver
serializes the native resource directory, and .NET's `ManagedPEBuilder` writes the final PE. DotKt's
adapter transports resource records and relocations rather than independently implementing their synthesis.

The removed placeholder's PDB was not a Kotlin source PDB. The SDK no longer generates or copies that
misleading file; actual Kotlin source debugging information remains separate compiler work.
Likewise, `GenerateDocumentationFile` does not produce a placeholder C# XML document: Kotlin XML documentation
generation is not implemented, and build/publish/pack do not advertise a nonexistent documentation output.

See [Microsoft's manifest compiler option](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/resources#win32manifest)
and [application manifests](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests).
