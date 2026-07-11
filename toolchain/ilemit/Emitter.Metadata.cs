// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// The BIR `attr`-node -> CustomAttributeBuilder path (BuildCab/TryCab/ConstArgValue) + parameter metadata. #71 S2:
// ilemit no longer GENERATES any Kotlin round-trip metadata — bir2cir (RoundtripMetadata) emits every [Kotlin*]/
// [Nullable]/[NullableContext] as an ordinary CIR `attrs` entry (and the attr-class DEFS as ordinary type decls);
// ilemit only STAMPS them dumbly through the generic BuildCab path below. No Kotlin-semantic decision remains here.
sealed partial class Emitter
{
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

    // Structured declaration-modifier lookup (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    // Replaces the scattered top-level boolean fields (isFun/isSealed/inline/infix/operator/suspend/vararg…).
    internal static bool ModFlag(JsonElement decl, string key)
        => decl.TryGetProperty("mods", out var mo) && mo.ValueKind == JsonValueKind.Object
           && mo.TryGetProperty(key, out var f) && f.ValueKind == JsonValueKind.True;

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
        // Ctor pick: arity match is the fallback (every current single-overload attr), refined to an EXACT runtime-type
        // match when several overloads share the arg count. This disambiguates the csc DUAL-ctor NullableAttribute —
        // (byte) vs (byte[]) both take 1 arg, so a plain count pick would route a byte[] arg to the (byte) ctor and
        // CustomAttributeBuilder would throw -> TryCab SILENTLY drops the stamp (a byte-equivalence trap). A null arg
        // skips the type check (falls back to arity).
        ConstructorInfo ctor = null;
        for (int i = 0; i < ti.Ctors.Count; i++)
        {
            var ps = ti.CtorDefs[i].GetProperty("params");
            if (ps.GetArrayLength() != args.Length) continue;
            ctor ??= ti.Ctors[i];
            int j = 0; bool exact = true;
            foreach (var p in ps.EnumerateArray())
            {
                if (args[j] != null && MapType(p.GetProperty("type")) != args[j].GetType()) { exact = false; break; }
                j++;
            }
            if (exact) { ctor = ti.Ctors[i]; break; }
        }
        ctor ??= ti.Ctors.Count > 0 ? ti.Ctors[0] : ti.TB.DefineDefaultConstructor(MethodAttributes.Public);
        return TryCab(ctor, args, attr);
    }

    // Stamp each CIR `attrs` entry of a field/property/return-parameter decl onto its builder — the SAME generic
    // BuildCab path the type/method/param sites use. Skips an attr whose type is neither clr:-imported nor emitted in
    // this assembly (BuildCab would KeyNotFound) — the CLR layer decides what is encodable. bir2cir (RoundtripMetadata)
    // folds the round-trip metadata ([KotlinReadOnly]/[KotlinSuspendFunctionType]/[Nullable]/…) into these arrays;
    // ilemit only STAMPS. `set` is FieldBuilder/PropertyBuilder/ParameterBuilder.SetCustomAttribute.
    void StampMemberAttrs(Action<CustomAttributeBuilder> set, JsonElement decl)
    {
        if (!decl.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Array) return;
        foreach (var a in attrs.EnumerateArray())
        {
            var an = a.GetProperty("attr").GetString();
            if (!an.StartsWith("clr:", StringComparison.Ordinal) && !_types.ContainsKey(an)) continue;
            var cab = BuildCab(a); if (cab != null) set(cab);
        }
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
        // A `bytes` arg-value kind (base64) -> a real byte[] fixed argument (a codec extension, NOT Kotlin knowledge):
        // bir2cir base64-encodes the carrier payloads ([KotlinInline]/[KotlinSuspendFunctionType] (version,byte[])) and
        // the nested NullableAttribute(byte[]) form through this. Mutually exclusive with `value`/`type`; its byte[]
        // runtime type drives BuildCab's exact-ctor pick above.
        if (e.TryGetProperty("bytes", out var bb)) return Convert.FromBase64String(bb.GetString());
        // Annotation arguments are always compile-time constants (const nodes).
        if (!e.TryGetProperty("value", out var v)) return null;
        var ty = e.TryGetProperty("type", out var tEl) ? PrimShorthandName(SlotName(tEl)) : null;
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
                    "sbyte" => (sbyte)v.GetInt32(),
                    "byte" => (byte)v.GetInt32(),   // an unsigned byte arg (NullableAttribute/NullableContextAttribute)
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
            // PARAMETER-level custom attributes — the generic BuildCab path. bir2cir (RoundtripMetadata) folds the
            // round-trip metadata into this array too ([Nullable] for a nullable-reference param, [KotlinSuspendFunctionType]
            // for a suspend fn-type param), so the parameter builder must be forced whenever attrs are present even if the
            // param otherwise carries no name/vararg/default. ilemit only STAMPS — no Kotlin-semantic decision here.
            JsonElement pattrs = default;
            bool hasAttrs = p.TryGetProperty("attrs", out pattrs) && pattrs.GetArrayLength() > 0;
            if (name.Length == 0 && !vararg && !hasDefault && !hasAttrs) { i++; continue; }
            // A constant default -> [Optional] + DefaultParameterValue, so a cross-module caller can omit the arg.
            var attrs = hasDefault ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : ParameterAttributes.None;
            var pb = defineParam(i, attrs, name.Length > 0 ? name : null);
            // `vararg xs: T` -> [ParamArray] so the .NET signature is a params array (a C# OR Kotlin consumer can spread).
            if (vararg) pb.SetCustomAttribute(new CustomAttributeBuilder(typeof(ParamArrayAttribute).GetConstructor(Type.EmptyTypes), new object[0]));
            if (hasDefault) { try { pb.SetConstant(ConstArgValue(dflt)); } catch { } }
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
