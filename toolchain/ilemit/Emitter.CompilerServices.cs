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

    // Define a sealed internal `: Attribute` (by FULL name) with a single constructor of the given parameter types,
    // embedded in this module. Metadata-only: the ctor body just chains to Attribute(); the APPLIED constructor
    // arguments live in the metadata blob (read via GetCustomAttributesData().ConstructorArguments), so no fields needed.
    Type DefineEmbeddedAttr(string fullName, params Type[] ctorParams)
    {
        var tb = _mod.DefineType(fullName,
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
    // what the stamp passes write, and what facadegen reads (by full name + constructor arg).
    void EnsureKotlinAttrs()
    {
        if (_kAttrsResolved) return;
        _kAttrsResolved = true;
        _kFuncAttr     = DefineEmbeddedAttr(CompilerServicesNs + "KotlinFunctionAttribute", typeof(int));   // infix/operator/suspend
        _kFileAttr     = DefineEmbeddedAttr(CompilerServicesNs + "KotlinFileClassAttribute");               // <File>Kt facade marker
        _kInlineAttr   = DefineEmbeddedAttr(CompilerServicesNs + "KotlinInlineAttribute", typeof(string));  // carried BIR body
        _kReadOnlyAttr = DefineEmbeddedAttr(CompilerServicesNs + "KotlinReadOnlyAttribute");                // public field, `val`
        // Reference-type nullability uses .NET's OWN NRT metadata (not a DotKt attribute), embedded under its standard
        // System.Runtime.CompilerServices names so a C# consumer recognizes it too — the csc model. [NullableContext(b)]
        // is the per-type default (we emit 1 = non-null); [Nullable(2)] overrides a specific nullable reference position.
        _nullableAttr    = DefineEmbeddedAttr("System.Runtime.CompilerServices.NullableAttribute", typeof(byte));
        _nullableCtxAttr = DefineEmbeddedAttr("System.Runtime.CompilerServices.NullableContextAttribute", typeof(byte));
    }

    // [NullableContext(1)] — the per-type default that ALL reference-type positions in the type are non-null (a nullable
    // one then carries its own [Nullable(2)] override). Mirrors how csc compresses NRT metadata.
    void ApplyNullableContext(TypeBuilder tb)
    {
        EnsureKotlinAttrs();
        tb.SetCustomAttribute(new CustomAttributeBuilder(_nullableCtxAttr.GetConstructor(new[] { typeof(byte) }), new object[] { (byte)1 }));
    }

    // [Nullable(2)] — marks a single reference-type position (a return/parameter) as nullable (`T?`).
    void ApplyNullable(ParameterBuilder pb)
    {
        EnsureKotlinAttrs();
        pb.SetCustomAttribute(new CustomAttributeBuilder(_nullableAttr.GetConstructor(new[] { typeof(byte) }), new object[] { (byte)2 }));
    }
}
