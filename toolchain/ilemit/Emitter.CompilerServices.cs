// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;

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
    Type DefineEmbeddedAttr(string fullName, params Type[] ctorParams) => DefineEmbeddedAttrN(fullName, new[] { ctorParams });

    // As DefineEmbeddedAttr, but defines SEVERAL constructor overloads on the one attribute type. Mirrors csc's
    // System.Runtime.CompilerServices.NullableAttribute, which carries BOTH a scalar `NullableAttribute(byte)` (a
    // single reference-type position) AND an array `NullableAttribute(byte[])` (a NESTED type, one flattened byte per
    // type node — e.g. `Task<string?>` -> {1,2}, outer non-null + inner nullable). ilemit needs both so a bridge
    // return whose nullable `?` rides an INNER type arg round-trips (facadegen reads the array form).
    Type DefineEmbeddedAttrN(string fullName, Type[][] ctorParamSets)
    {
        var tb = _mod.DefineType(fullName,
            TypeAttributes.NotPublic | TypeAttributes.Sealed | TypeAttributes.Class, typeof(Attribute));
        // Attribute's parameterless constructor is PROTECTED, so it needs non-public binding flags to resolve.
        var baseCtor = typeof(Attribute).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        foreach (var ctorParams in ctorParamSets)
        {
            var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, ctorParams);
            var il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, baseCtor);
            il.Emit(OpCodes.Ret);
        }
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
        _kInlineAttr   = DefineEmbeddedAttr(CompilerServicesNs + "KotlinInlineAttribute", typeof(string), typeof(byte[]));  // carried BIR body: (version, content)
        _kReadOnlyAttr = DefineEmbeddedAttr(CompilerServicesNs + "KotlinReadOnlyAttribute");                // public field, `val`
        // Round-trip class-nature markers with no faithful .NET analog (a `fun interface`->plain interface, a `sealed`
        // class/interface->abstract-class/interface). Metadata-only, read back by facadegen to restore the Kotlin nature.
        _kFunIfaceAttr = DefineEmbeddedAttr(CompilerServicesNs + "KotlinFunInterfaceAttribute");            // `fun interface` (SAM)
        _kSealedAttr   = DefineEmbeddedAttr(CompilerServicesNs + "KotlinSealedAttribute");                  // `sealed` class/interface
        // H2: a `suspend (…) -> T` function TYPE in a param/return/field/property POSITION. bir2cir erases the `sfunc:`
        // token to `object` in the CLR signature (a suspend-lambda VALUE is a Continuation-based SM object, not a Func),
        // which destroys the suspend origin AND its shape (arg/return types). This attribute carries the ORIGINAL
        // `fn` TypeNode SHAPE (not a bare flag — the erased CLR type is `object`, so a flag alone could not
        // reconstruct the function type) so a re-consuming Kotlin module (facadegen reads it back) can restore the
        // `suspend (…) -> T` type. Metadata-only; a C# consumer ignores it. Rides the SAME versioned `(version, byte[])`
        // carrier envelope as KotlinInline (spec §0), so a future binary codec covers both.
        _kSuspendFnAttr = DefineEmbeddedAttr(CompilerServicesNs + "KotlinSuspendFunctionTypeAttribute", typeof(string), typeof(byte[]));
        // Reference-type nullability uses .NET's OWN NRT metadata (not a DotKt attribute), embedded under its standard
        // System.Runtime.CompilerServices names so a C# consumer recognizes it too — the csc model. [NullableContext(b)]
        // is the per-type default (we emit 1 = non-null); [Nullable(2)] overrides a specific nullable reference position.
        _nullableAttr    = DefineEmbeddedAttrN("System.Runtime.CompilerServices.NullableAttribute",
                               new[] { new[] { typeof(byte) }, new[] { typeof(byte[]) } });
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

    // [Nullable(new byte[]{...})] — the NESTED form: one flattened byte per type node (0=oblivious, 1=non-null,
    // 2=nullable), pre-order. Used when the nullable `?` rides an INNER type-arg rather than the top-level type, e.g.
    // a `suspend fun f(): String?`'s CLR bridge return `Task<string?>` -> {1,2}. A single-element (or all-equal)
    // array is the scalar case; csc collapses it, but the array ctor is equally valid metadata, so callers may pass
    // either. `flags` is supplied verbatim by bir2cir (which owns the Kotlin->CLR nullability walk); ilemit only stamps.
    void ApplyNullable(ParameterBuilder pb, byte[] flags)
    {
        if (flags == null || flags.Length == 0) return;
        EnsureKotlinAttrs();
        if (flags.Length == 1) { ApplyNullable(pb, flags[0]); return; }
        pb.SetCustomAttribute(new CustomAttributeBuilder(_nullableAttr.GetConstructor(new[] { typeof(byte[]) }), new object[] { flags }));
    }

    // [Nullable(b)] — scalar with an explicit byte (2=nullable is the common case; 1=non-null appears inside a
    // collapsed nested walk). Kept distinct from the byte[] overload so a genuine single position stays the compact form.
    void ApplyNullable(ParameterBuilder pb, byte b)
    {
        EnsureKotlinAttrs();
        pb.SetCustomAttribute(new CustomAttributeBuilder(_nullableAttr.GetConstructor(new[] { typeof(byte) }), new object[] { b }));
    }

    // [KotlinSuspendFunctionType(version, content)] — H2. Stamp the ORIGINAL `fn` TypeNode shape of a suspend
    // function-type position (a param/return via a ParameterBuilder; a field/property below). `shape` is supplied
    // verbatim by bir2cir (the `suspendFnType`/`retSuspendFnType` CIR fact), which owns the Kotlin->CLR erasure and
    // therefore the only place the pre-erasure shape survives. Rides the SAME versioned `(version, byte[])` carrier
    // envelope as KotlinInline (spec §0). ilemit only stamps.
    CustomAttributeBuilder SuspendFnAttr(string shape)
    {
        EnsureKotlinAttrs();
        byte[] content = DotKt.Bir.BirCarrier.EncodeBody(DotKt.Bir.BirCarrier.JsonV1, System.Text.Json.Nodes.JsonNode.Parse(shape)!);
        return new CustomAttributeBuilder(
            _kSuspendFnAttr.GetConstructor(new[] { typeof(string), typeof(byte[]) }),
            new object[] { DotKt.Bir.BirCarrier.JsonV1, content });
    }
    void ApplySuspendFnType(ParameterBuilder pb, string shape)
    {
        if (string.IsNullOrEmpty(shape)) return;
        pb.SetCustomAttribute(SuspendFnAttr(shape));
    }
    void ApplySuspendFnType(FieldBuilder fb, string shape)
    {
        if (string.IsNullOrEmpty(shape)) return;
        fb.SetCustomAttribute(SuspendFnAttr(shape));
    }
    void ApplySuspendFnType(PropertyBuilder pb, string shape)
    {
        if (string.IsNullOrEmpty(shape)) return;
        pb.SetCustomAttribute(SuspendFnAttr(shape));
    }

    // Read a `(string version, byte[] content)` carrier attribute (spec §0: KotlinInline / KotlinSuspendFunctionType)
    // back to its decoded JSON string. Routes through the single BirCarrier.DecodeBody dispatch — an UNKNOWN version
    // throws (loud, never a silent mis-decode).
    static string DecodeCarrier(CustomAttributeData cad)
    {
        var version = (string)cad.ConstructorArguments[0].Value!;
        var content = ReadByteArrayArg(cad.ConstructorArguments[1]);
        return DotKt.Bir.BirCarrier.DecodeBody(version, content).ToJsonString();
    }

    // A reflected byte[] constructor argument materializes as an IReadOnlyList<CustomAttributeTypedArgument> (each
    // element's .Value is a boxed byte), not a byte[] — reify it.
    static byte[] ReadByteArrayArg(CustomAttributeTypedArgument a)
    {
        if (a.Value is byte[] b) return b;
        if (a.Value is System.Collections.Generic.IReadOnlyList<CustomAttributeTypedArgument> arr)
        {
            var r = new byte[arr.Count];
            for (int i = 0; i < arr.Count; i++) r[i] = (byte)arr[i].Value!;
            return r;
        }
        throw new FormatException("carrier content is not a byte[]");
    }

    // Read a CIR `retNullableFlags`/`nullableFlags` JSON array (bir2cir's flattened NullableAttribute byte walk) into
    // a byte[]. Each element is 0 (oblivious) / 1 (non-null ref) / 2 (nullable ref) per type node, pre-order.
    static byte[] ReadNullableFlags(JsonElement arr)
    {
        var n = arr.GetArrayLength();
        var flags = new byte[n];
        int i = 0;
        foreach (var b in arr.EnumerateArray()) flags[i++] = (byte)b.GetInt32();
        return flags;
    }
}
