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

The path is project-relative (absolute paths and spaces also work). DotKt preserves the native PE resources
produced by the SDK's C# compilation of its placeholder, including the resolved `Win32Manifest` bytes as
`RT_MANIFEST`. Only native resources are transferred; the final declarations, metadata and method bodies
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

With no selected manifest, executable projects (`Exe`/`WinExe`) receive the C# compiler's default
`asInvoker` manifest. Library projects do not receive a default application manifest. Set
`<NoWin32Manifest>true</NoWin32Manifest>` to disable the default, for example when providing it in a later
build step. Explicit manifest and resource options follow the underlying SDK/compiler's selection and
conflict rules; DotKt does not overlay another manifest on that result. In particular, `asInvoker` is an
execution-policy declaration, not an unconditional resource to add to every DLL.

Native `VERSIONINFO` is also preserved, including SDK-selected file/product version and product/company
strings. This is the Windows Explorer version-information surface, distinct from CLR assembly identity
and managed attributes. Currently, SDK version properties are not projected to the Kotlin assembly's
managed version identity or version attributes; this resource transfer does not address that separate limitation.
Existing SDK `ApplicationIcon` and `Win32Resource` inputs are preserved by the same
resource transfer; they are not independently reconstructed or merged. Switching or clearing native
resource options refreshes the provider and final PE without recompiling Kotlin. Direct `ilemit` callers
have no implicit resources: the project driver supplies a PE via `--win32-resources-from` when needed.

See [Microsoft's manifest compiler option](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/resources#win32manifest)
and [application manifests](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests).
