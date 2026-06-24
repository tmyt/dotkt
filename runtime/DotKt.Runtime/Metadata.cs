namespace DotKt.Runtime.CompilerServices
{
    /// <summary>
    /// Marks an emitted assembly so a consumer can import a Kotlin package whose .NET namespace differs (e.g. a library
    /// projected to `DotKt.Coroutines` is imported via `import kotlinx.coroutines.*`).
    /// </summary>
    /// <remarks>
    /// The other DotKt round-trip metadata attributes ([KotlinFunction]/[KotlinFileClass]/[KotlinInline]/
    /// [KotlinNullable]/[KotlinReadOnly]) are compiler-EMBEDDED per assembly (like csc's NullableAttribute) — see
    /// toolchain/ilemit/Emitter.CompilerServices.cs. This one is the exception: it is ASSEMBLY-level, and
    /// PersistedAssemblyBuilder corrupts the image when an assembly-level attribute references a module-internal type,
    /// so it stays a real referenced type here (ilemit resolves it from a --ref'd DotKt.Runtime to stamp).
    /// </remarks>
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public sealed class DotKtNamespaceProjectionAttribute : System.Attribute
    {
        public string KotlinPrefix { get; }
        public string DotNetPrefix { get; }
        public DotKtNamespaceProjectionAttribute(string kotlinPrefix, string dotNetPrefix)
        {
            KotlinPrefix = kotlinPrefix;
            DotNetPrefix = dotNetPrefix;
        }
    }
}
