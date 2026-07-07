// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// The `[Kotlin*]` round-trip metadata attributes (from DotKt.Runtime) and the BIR `attr`-node constant decoding.
sealed partial class Emitter
{
    // The embedded `DotKt.Runtime.CompilerServices.*` attribute types — defined into THIS module by EnsureKotlinAttrs
    // (Emitter.CompilerServices.cs). Always available once defined (no external reference needed to stamp).
    bool _kAttrsResolved;
    Type _kFuncAttr, _kFileAttr, _kInlineAttr, _kReadOnlyAttr, _kFunIfaceAttr, _kSealedAttr, _nullableAttr, _nullableCtxAttr, _kSuspendFnAttr;

    // Fields carrying `@kotlin.concurrent.Volatile` (kotc emits `"volatile":true`). Emitted with a REQUIRED
    // `modreq(System.Runtime.CompilerServices.IsVolatile)` custom modifier — EXACTLY how C# encodes a `volatile`
    // field — so the JIT treats every access as volatile; the `volatile.` IL prefix is additionally emitted before
    // ld/st on these fields (MaybeVolatile). Populated at DefineField (Program.cs pass3 field declaration).
    readonly HashSet<FieldInfo> _volatileFields = new();

    // DefineField with a `modreq(IsVolatile)` required custom modifier (the C# `volatile` shape); tracks the field so
    // access sites can emit the matching `volatile.` prefix.
    FieldBuilder DefineVolatileField(TypeBuilder tb, string name, Type type, FieldAttributes attrs)
    {
        var fb = tb.DefineField(name, type, new[] { typeof(System.Runtime.CompilerServices.IsVolatile) }, null, attrs);
        _volatileFields.Add(fb);
        return fb;
    }

    // Emit the `volatile.` prefix before a ld/st opcode when the field is volatile (no-op otherwise). Pairs with the
    // `modreq(IsVolatile)` on the field itself — this is what the C# compiler emits for a `volatile` field access.
    void MaybeVolatile(FieldInfo fld) { if (fld != null && _volatileFields.Contains(fld)) _il.Emit(OpCodes.Volatile); }

    // [KotlinReadOnly] — a public backing field whose Kotlin property isn't publicly settable (restore as `val`).
    void ApplyKotlinReadOnly(FieldBuilder fb)
    {
        EnsureKotlinAttrs();
        fb.SetCustomAttribute(new CustomAttributeBuilder(_kReadOnlyAttr.GetConstructor(Type.EmptyTypes), new object[0]));
    }

    // [KotlinInline(body)] — the inline+lambda fn's BIR body, for cross-module splicing.
    void ApplyKotlinInline(MethodBuilder mb, string body)
    {
        EnsureKotlinAttrs();
        mb.SetCustomAttribute(new CustomAttributeBuilder(_kInlineAttr.GetConstructor(new[] { typeof(string) }), new object[] { body }));
    }

    // Structured declaration-modifier lookup (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    // Replaces the scattered top-level boolean fields (isFun/isSealed/inline/infix/operator/suspend/vararg…).
    internal static bool ModFlag(JsonElement decl, string key)
        => decl.TryGetProperty("mods", out var mo) && mo.ValueKind == JsonValueKind.Object
           && mo.TryGetProperty(key, out var f) && f.ValueKind == JsonValueKind.True;

    // [KotlinFunction(flags)] — Kotlin modifiers with no .NET analog (infix/operator/suspend), for Kotlin re-consumption.
    void ApplyKotlinFunction(MethodBuilder mb, int flags)
    {
        EnsureKotlinAttrs();
        mb.SetCustomAttribute(new CustomAttributeBuilder(_kFuncAttr.GetConstructor(new[] { typeof(int) }), new object[] { flags }));
    }

    // [KotlinFileClass] — marks a `<File>Kt` facade so its statics restore as top-level Kotlin functions.
    void ApplyKotlinFileClass(TypeBuilder tb)
    {
        EnsureKotlinAttrs();
        tb.SetCustomAttribute(new CustomAttributeBuilder(_kFileAttr.GetConstructor(Type.EmptyTypes), new object[0]));
    }

    // [KotlinFunInterface] — marks an interface that was a Kotlin `fun interface` (SAM), so a re-consuming Kotlin
    // module restores it as a functional interface and can pass a lambda where it's expected.
    void ApplyKotlinFunInterface(TypeBuilder tb)
    {
        EnsureKotlinAttrs();
        tb.SetCustomAttribute(new CustomAttributeBuilder(_kFunIfaceAttr.GetConstructor(Type.EmptyTypes), new object[0]));
    }

    // [KotlinSealed] — marks a type that was a Kotlin `sealed` class/interface (it lowers to a CLR abstract class /
    // interface, which loses the sealed modality), so a re-consuming Kotlin module restores `Modality.SEALED`.
    void ApplyKotlinSealed(TypeBuilder tb)
    {
        EnsureKotlinAttrs();
        tb.SetCustomAttribute(new CustomAttributeBuilder(_kSealedAttr.GetConstructor(Type.EmptyTypes), new object[0]));
    }

    // Returns null when the CLR custom-attribute encoder cannot represent this annotation's shape (so the caller
    // skips it). kotc emits EVERY annotation verbatim (it is just metadata to the frontend); the CLR layer decides
    // what is encodable. Some Kotlin annotations have a constructor-parameter type that Reflection.Emit's
    // CustomAttributeBuilder rejects at validation — e.g. a generic-instantiation parameter, where
    // TypeBuilderInstantiation.IsSubclassOf throws NotSupportedException. Such an attribute carries no CLR-attribute
    // semantics we could preserve anyway, so we skip it with a diagnostic rather than abort the whole emit.
    CustomAttributeBuilder BuildCab(JsonElement a)
    {
        var attr = a.GetProperty("attr").GetString();
        var args = a.GetProperty("args").EnumerateArray().Select(ConstArgValue).ToArray();
        if (attr.StartsWith("clr:"))
        {
            // An imported .NET attribute (#54): bind its real constructor (resolved by the declared arg types,
            // falling back to arity) and apply it with the constant args.
            var at = ClrRef(attr);
            var argTypes = a.GetProperty("argTypes").EnumerateArray().Select(s => ClrRef(s)).ToArray();
            var nctor = at.GetConstructor(argTypes)
                        ?? at.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == args.Length);
            return TryCab(nctor, args, attr);
        }
        // The attribute type must be emitted in THIS assembly (present in _types). A stdlib-only annotation that the app
        // merely APPLIES — e.g. `@kotlin.OptIn(ExperimentalAtomicApi::class)` opting into an experimental stdlib API — is
        // NOT defined here, so `_types[attr]` would KeyNotFound. Skip it (like an un-encodable attr): it is a compile-time
        // opt-in marker with no need to survive into the app's IL.
        if (!_types.ContainsKey(attr))
        {
            Console.Error.WriteLine($"ilemit: skipping custom attribute [{attr}]: type not emitted in this assembly");
            return null;
        }
        var ti = _types[attr];
        // Ensure the attribute type's ctors are defined even if this stamp runs before pass 3 reaches that type
        // (a `@KotlinDefault` on an earlier type's parameter), then pick the ctor whose parameter count matches the
        // applied argument count (an annotation may have >1 ctor). Only a genuinely ctor-less type mints a default one.
        EnsureCtorsDefined(ti);
        ConstructorInfo ctor = null;
        for (int i = 0; i < ti.Ctors.Count; i++)
            if (ti.CtorDefs[i].GetProperty("params").GetArrayLength() == args.Length) { ctor = ti.Ctors[i]; break; }
        ctor ??= ti.Ctors.Count > 0 ? ti.Ctors[0] : ti.TB.DefineDefaultConstructor(MethodAttributes.Public);
        return TryCab(ctor, args, attr);
    }

    CustomAttributeBuilder TryCab(ConstructorInfo ctor, object[] args, string attr)
    {
        try { return new CustomAttributeBuilder(ctor, args); }
        catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException)
        {
            Console.Error.WriteLine($"ilemit: skipping un-encodable custom attribute [{attr}]: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    static object ConstArgValue(JsonElement e)
    {
        // Annotation arguments are always compile-time constants (const nodes).
        if (!e.TryGetProperty("value", out var v)) return null;
        var ty = e.TryGetProperty("type", out var tEl) ? SlotName(tEl) : null;
        switch (v.ValueKind)
        {
            // A `char` default may arrive as its single-char STRING form (`' '` -> "  ") — SetConstant needs a real
            // `char`, not a string, or a cross-module caller stamps `ldstr " "` for a char param (InvalidProgram).
            case JsonValueKind.String:
                var sv = v.GetString();
                return (ty == "char" && sv.Length > 0) ? (object)sv[0] : sv;
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            case JsonValueKind.Number:
                return ty switch
                {
                    "long" => (object)v.GetInt64(),
                    "double" => v.GetDouble(),
                    "float" => (float)v.GetDouble(),
                    "short" => (short)v.GetInt32(),
                    "byte" => (sbyte)v.GetInt32(),
                    "char" => (char)v.GetInt32(),   // a char default given as its numeric code point
                    _ => v.GetInt32(),
                };
            default: return null;
        }
    }

    // Emit parameter NAMES into the metadata (DefineParameter is 1-based; 0 = return). ilemit otherwise defines
    // methods by type only, so the names are lost — and facadegen falls back to arg0/arg1, which blocks named-argument
    // calls across an assembly boundary. The names come straight from the BIR params.
    void DefineParamNames(MethodBuilder mb, JsonElement m) => DefineParamNames(mb.DefineParameter, m);
    void DefineParamNames(ConstructorBuilder cb, JsonElement m) => DefineParamNames(cb.DefineParameter, m);
    void DefineParamNames(Func<int, ParameterAttributes, string, ParameterBuilder> defineParam, JsonElement m)
    {
        if (!m.TryGetProperty("params", out var ps)) return;
        int i = 1;
        foreach (var p in ps.EnumerateArray())
        {
            var name = (p.TryGetProperty("name", out var nn) ? nn.GetString() : null) ?? "";
            bool vararg = ModFlag(p, "vararg");
            bool hasDefault = p.TryGetProperty("default", out var dflt);
            // A nullable reference parameter needs a [Nullable(2)] override against the type's non-null default, so the
            // parameter builder must exist even if it otherwise carries no name/vararg/default. (A value-type `X?` is the
            // structural Nullable<X> instead; the [Nullable] on it is simply ignored by readers — harmless.)
            // #37/#48: nullability now rides the Type node ONLY — bir2cir ALWAYS supplies the flattened NRT byte walk in
            // `nullableFlags` for every reference-nullable param (the scalar decl-level `"nullable"` flag is retired).
            byte[] pFlags = p.TryGetProperty("nullableFlags", out var pnf) && pnf.ValueKind == JsonValueKind.Array ? ReadNullableFlags(pnf) : null;
            // H2: a `suspend (…) -> T` PARAMETER type — bir2cir carries the pre-erasure `sfunc:` shape in `suspendFnType`
            // (the CLR param type itself is the erased `object`). Force the parameter builder so [KotlinSuspendFunctionType]
            // can be stamped even if the param otherwise carries no name/default/nullability.
            string pSuspendFn = p.TryGetProperty("suspendFnType", out var psf) ? psf.GetRawText() : null;
            // PARAMETER-level custom attributes (e.g. [ClrRefArgument], which bir2cir reads from the ref.dll to pass the
            // arg by reference). Stripped in the runtime build (kotc emits none), so this rides only the ref.dll.
            JsonElement pattrs = default;
            bool hasAttrs = !_stripMetadata && p.TryGetProperty("attrs", out pattrs) && pattrs.GetArrayLength() > 0;
            if (name.Length == 0 && !vararg && !hasDefault && pFlags == null && !hasAttrs && string.IsNullOrEmpty(pSuspendFn)) { i++; continue; }
            // A constant default -> [Optional] + DefaultParameterValue, so a cross-module caller can omit the arg.
            var attrs = hasDefault ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : ParameterAttributes.None;
            var pb = defineParam(i, attrs, name.Length > 0 ? name : null);
            // `vararg xs: T` -> [ParamArray] so the .NET signature is a params array (a C# OR Kotlin consumer can spread).
            if (vararg) pb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
            if (hasDefault) { try { pb.SetConstant(ConstArgValue(dflt)); } catch { } }
            if (pFlags != null) ApplyNullable(pb, pFlags);
            if (!string.IsNullOrEmpty(pSuspendFn)) ApplySuspendFnType(pb, pSuspendFn);   // H2 suspend fn-type param shape
            // Apply each param attribute whose type this assembly can encode (in-assembly emitted type or a clr:-imported
            // one); an attr referencing a type not in `_types` is skipped (BuildCab would KeyNotFound) — the same "the CLR
            // layer decides what is encodable" policy the method-level attr path uses.
            if (hasAttrs)
                foreach (var a in pattrs.EnumerateArray())
                {
                    var an = a.GetProperty("attr").GetString();
                    if (!an.StartsWith("clr:", StringComparison.Ordinal) && !_types.ContainsKey(an)) continue;
                    var cab = BuildCab(a); if (cab != null) pb.SetCustomAttribute(cab);
                }
            i++;
        }
    }
}
