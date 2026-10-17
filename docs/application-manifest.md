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

The path is project-relative (absolute paths and spaces also work). DotKt consumes the .NET SDK's resolved
`Win32Manifest` input and writes its bytes as a native PE `RT_MANIFEST` resource. The standard SDK apphost
then carries it into the final Windows executable for build and publish. Executable outputs use resource ID 1;
Library outputs use DLL isolation-aware resource ID 2, matching C#. No managed `EmbeddedResource`,
sidecar copy or post-build patch is needed. Changing the file, switching it or clearing the property is an
incremental emission input; unchanged builds do not re-emit, and manifest-only changes do not recompile Kotlin.

The final-executable behavior applies to normal `Exe`/`WinExe` managed applications with a Windows PE apphost. `UseAppHost=false`
leaves the resource in the managed DLL only; launching that DLL with `dotnet` does not change the manifest
of the `dotnet` host process. A non-Windows apphost cannot use a Windows manifest. NativeAOT, ClickOnce,
icons and general Win32 resource files are not added by this feature. With no selected manifest, existing
output behavior is unchanged. Missing or unreadable declared files fail the build. Like the C# compiler,
the emitter transports the supplied bytes rather than validating the Windows manifest schema or OS policy.

See [Microsoft's manifest compiler option](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/compiler-options/resources#win32manifest)
and [application manifests](https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests).
