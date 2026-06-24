// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;

// Synthesizes the `DotKt.Runtime.CompilerServices.*` round-trip metadata attributes and EMBEDS them (internal) into
// the emitted assembly — the same model the C# compiler uses for its own compiler-generated attributes
// (System.Runtime.CompilerServices.NullableAttribute / IsReadOnlyAttribute / …). Rationale: these attributes are
// METADATA-ONLY — read at compile time by facadegen/the FIR injector, never executed — so they don't belong in a
// referenced runtime library. Embedding makes each assembly self-contained for its own round-trip metadata and
// removes the "must --ref DotKt.Runtime so ilemit can resolve the attribute type to stamp it" coupling (the class of
// bug where a missing reference silently skipped stamping). DotKt.Runtime then carries only executed code.
sealed partial class Emitter
{
    const string CompilerServicesNs = "DotKt.Runtime.CompilerServices.";

    // Define a sealed internal `: Attribute` with a single constructor of the given parameter types, embedded in this
    // module. Metadata-only: the ctor body just chains to Attribute(); the APPLIED constructor arguments live in the
    // metadata blob (read back via GetCustomAttributesData().ConstructorArguments), so no fields/properties are needed.
    Type DefineEmbeddedAttr(string simpleName, params Type[] ctorParams)
    {
        var tb = _mod.DefineType(CompilerServicesNs + simpleName,
            TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Class, typeof(Attribute));
        var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, ctorParams);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        // Attribute's parameterless constructor is PROTECTED, so it needs non-public binding flags to resolve.
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null));
        il.Emit(OpCodes.Ret);
        return tb.CreateType();
    }

    // Define + cache the embedded attribute types once per emitted module (idempotent). Their ctor signatures match
    // what Apply*/the assembly stamp pass write, and what facadegen reads (by full name + constructor arg).
    void EnsureKotlinAttrs()
    {
        if (_kAttrsResolved) return;
        _kAttrsResolved = true;
        _kFuncAttr     = DefineEmbeddedAttr("KotlinFunctionAttribute", typeof(int));      // infix/operator/suspend bitmask
        _kFileAttr     = DefineEmbeddedAttr("KotlinFileClassAttribute");                  // <File>Kt facade marker
        _kInlineAttr   = DefineEmbeddedAttr("KotlinInlineAttribute", typeof(string));     // carried BIR body
        _kNullableAttr = DefineEmbeddedAttr("KotlinNullableAttribute", typeof(uint));     // signature nullability mask
        _kReadOnlyAttr = DefineEmbeddedAttr("KotlinReadOnlyAttribute");                   // public field, `val` property
        // DotKtNamespaceProjection is ASSEMBLY-level; an embedded (module-internal) type in an assembly attribute
        // corrupts the PE under PersistedAssemblyBuilder, so it stays a real referenced type in DotKt.Runtime and is
        // resolved (null if DotKt.Runtime wasn't --ref'd, in which case --ns-projection is a no-op).
        _kNsProjAttr   = TryResolveType("DotKt.Runtime.CompilerServices.DotKtNamespaceProjectionAttribute");
    }
}
