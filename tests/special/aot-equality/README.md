Publish and **run the native executable** with the SDK under test, for example from the repository root:

```sh
dotnet publish tests/special/aot-equality/host/AotEquality.csproj -c Release -r linux-x64 -o build/aot-equality
build/aot-equality/AotEquality
```

The host intentionally does not root any reflection metadata. JIT execution alone cannot detect a trimmed virtual slot.
