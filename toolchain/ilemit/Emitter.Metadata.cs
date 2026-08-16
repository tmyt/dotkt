// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
#nullable enable annotations
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

// The BIR `attr`-node -> ECMA-335 custom-attribute blob path (BuildAttribute/TryAttribute/ConstArgValue) + parameter
// metadata. #71 S2:
// ilemit no longer GENERATES any Kotlin round-trip metadata — bir2cir (RoundtripMetadata) emits every [Kotlin*]/
// [Nullable]/[NullableContext] as an ordinary CIR `attrs` entry (standard attrs resolve from the target BCL);
// ilemit only STAMPS them dumbly through the generic BuildAttribute path below. No Kotlin-semantic decision remains.
sealed partial class Emitter
{
    sealed record EncodedAttribute(ConstructorInfo Constructor, byte[] Blob);
    sealed record NamedAttributeArg(bool Field, string Name, Type Type, object Value);

    // Fields carrying `@kotlin.concurrent.Volatile` (kotc emits `"volatile":true`). Emitted with a REQUIRED
    // `modreq(System.Runtime.CompilerServices.IsVolatile)` custom modifier — EXACTLY how C# encodes a `volatile`
    // field — so the JIT treats every access as volatile; the `volatile.` IL prefix is additionally emitted before
    // ld/st on these fields (MaybeVolatile). Populated at DefineField (Program.cs pass3 field declaration).
    readonly HashSet<FieldInfo> _volatileFields = new();

    // DefineField with a `modreq(IsVolatile)` required custom modifier (the C# `volatile` shape); tracks the field so
    // access sites can emit the matching `volatile.` prefix.
    FieldBuilder DefineVolatileField(TypeBuilder tb, string name, Type type, FieldAttributes attrs)
    {
        var fb = tb.DefineField(name, type, new[] { Bcl("System.Runtime.CompilerServices.IsVolatile") }, null, attrs);
        _volatileFields.Add(fb);
        return fb;
    }

    // Emit the `volatile.` prefix before a ld/st opcode when the field is volatile (no-op otherwise). Pairs with the
    // `modreq(IsVolatile)` on the field itself — this is what the C# compiler emits for a `volatile` field access.
    void MaybeVolatile(FieldInfo fld, JsonElement? access = null)
    {
        var isVolatile = fld != null && _volatileFields.Contains(fld) ||
            access is JsonElement carried && carried.TryGetProperty("volatile", out var marker) &&
            marker.ValueKind == JsonValueKind.True;
        if (isVolatile) _il.Emit(OpCodes.Volatile);
    }

    object LiteralConstant(JsonElement value, Type type)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (type == Bcl("System.String")) return value.GetString();
        if (type == Bcl("System.Boolean")) return value.GetBoolean();
        if (type == Bcl("System.Char")) return value.ValueKind == JsonValueKind.String
            ? value.GetString()[0] : (char)value.GetInt32();
        if (type == Bcl("System.SByte")) return (sbyte)value.GetInt32();
        if (type == Bcl("System.Byte")) return (byte)value.GetInt32();
        if (type == Bcl("System.Int16")) return (short)value.GetInt32();
        if (type == Bcl("System.UInt16")) return (ushort)value.GetInt32();
        if (type == Bcl("System.Int32")) return value.GetInt32();
        if (type == Bcl("System.UInt32")) return unchecked((uint)value.GetInt32());
        if (type == Bcl("System.Int64")) return value.GetInt64();
        if (type == Bcl("System.UInt64")) return unchecked((ulong)value.GetInt64());
        if (type == Bcl("System.Single")) return value.ValueKind == JsonValueKind.String
            ? float.Parse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture) : value.GetSingle();
        if (type == Bcl("System.Double")) return value.ValueKind == JsonValueKind.String
            ? double.Parse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture) : value.GetDouble();
        throw new InvalidOperationException($"unsupported literal field type '{type}'");
    }

    // Structured declaration-modifier lookup (spec §2.1): `decl.mods.<key> == true` (absent object/key = false).
    // Replaces the scattered top-level boolean fields (isFun/isSealed/inline/infix/operator/suspend/vararg…).
    internal static bool ModFlag(JsonElement decl, string key)
        => decl.TryGetProperty("mods", out var mo) && mo.ValueKind == JsonValueKind.Object
           && mo.TryGetProperty(key, out var f) && f.ValueKind == JsonValueKind.True;

    // Returns null when the CLR custom-attribute encoder cannot represent this annotation's shape (so the caller
    // skips it). kotc emits EVERY annotation verbatim (it is just metadata to the frontend); the CLR layer decides
    // what is encodable. The blob is encoded here instead of through CustomAttributeBuilder: CAB validates target-MLC
    // parameter Types against host-runtime argument Types, which would reintroduce the mixed-universe bug #336 removes.
    EncodedAttribute? BuildAttribute(JsonElement a)
    {
        var attr = SlotName(a.GetProperty("attr"));   // `attr` is a structured `{t:fqn}` identity node (#48)
        var args = a.GetProperty("args").EnumerateArray().Select(ConstArgValue).ToArray();
        var namedArgs = a.TryGetProperty("namedArgs", out var named)
            ? named.EnumerateArray().Select(item => new NamedAttributeArg(
                item.GetProperty("kind").GetString() == "field",
                item.GetProperty("name").GetString(),
                ClrRef(item.GetProperty("type")),
                ConstArgValue(item.GetProperty("value")))).ToArray()
            : Array.Empty<NamedAttributeArg>();
        if (a.TryGetProperty("attrExternal", out var extF) && extF.GetBoolean())
        {
            // An EXTERNAL .NET attribute (#54/#48): its type lives in a referenced assembly, not this one. The carried
            // declared parameter vector must identify its constructor exactly; an ABI miss is not repaired by arity.
            var at = a.TryGetProperty("attrAssembly", out var attrAssembly)
                ? _target.ResolveType(attr, attrAssembly.GetString())
                : ClrRef(attr);
            var argTypes = a.GetProperty("argTypes").EnumerateArray().Select(s => ClrRef(s)).ToArray();
            // An applied attribute is a call, and the constructor it calls is named like any other member.
            // Selecting it from the declared argument vector was the last place a blob encoder chose a member.
            if (PrimaryFromRef(a, "memberRef") is not ConstructorInfo nctor)
                throw new InvalidOperationException(
                    $"ilemit: applied attribute [{attr}] carries no resolved member reference. Every external "
                    + "member arrives named; a node without one is an earlier-layer drop (#370)");
            return TryAttribute(nctor, argTypes, args, namedArgs, attr);
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
        // (a `@KotlinDefault` on an earlier type's parameter), then link the exact carried declaration signature.
        EnsureCtorsDefined(ti);
        var declaredArgTypes = a.GetProperty("argTypes").EnumerateArray()
            .Select(t => SigCanon(DotKt.Bir.TypeNode.Read(t))).ToArray();
        var hits = new List<(ConstructorInfo ctor, Type[] parameterTypes)>();
        for (int i = 0; i < ti.Ctors.Count; i++)
        {
            var ps = ti.CtorDefs[i].GetProperty("params");
            if (ps.GetArrayLength() != declaredArgTypes.Length) continue;
            var parameterNodes = ps.EnumerateArray().Select(p => p.GetProperty("type")).ToArray();
            if (!parameterNodes.Select(t => SigCanon(DotKt.Bir.TypeNode.Read(t))).SequenceEqual(declaredArgTypes)) continue;
            hits.Add((ti.Ctors[i], parameterNodes.Select(MapType).ToArray()));
        }
        if (hits.Count == 0)
            return TryAttribute(null, Array.Empty<Type>(), args, namedArgs, attr); // existing policy: an unencodable source annotation is skipped
        if (hits.Count > 1)
            throw new InvalidOperationException($"ilemit: attribute constructor descriptor {attr}{a.GetProperty("argTypes").GetRawText()} links {hits.Count} local declarations");
        return TryAttribute(hits[0].ctor, hits[0].parameterTypes, args, namedArgs, attr);
    }

    // Stamp each CIR `attrs` entry of a field/property/return-parameter decl onto its builder — the SAME generic
    // BuildAttribute path the type/method/param sites use. Skips an attr whose type is neither `attrExternal`-flagged nor
    // emitted in this assembly (BuildAttribute would KeyNotFound) — the CLR layer decides what is encodable. bir2cir (RoundtripMetadata)
    // folds the round-trip metadata ([KotlinReadOnly]/[KotlinSuspendFunctionType]/[Nullable]/…) into these arrays;
    // ilemit only STAMPS. `set` is FieldBuilder/PropertyBuilder/ParameterBuilder.SetCustomAttribute.
    void StampMemberAttrs(Action<ConstructorInfo, byte[]> set, JsonElement decl)
    {
        if (!decl.TryGetProperty("attrs", out var attrs) || attrs.ValueKind != JsonValueKind.Array) return;
        foreach (var a in attrs.EnumerateArray())
        {
            var an = SlotName(a.GetProperty("attr"));   // structured `{t:fqn}` identity node (#48)
            var anExternal = a.TryGetProperty("attrExternal", out var anExt) && anExt.GetBoolean();
            if (!anExternal && !_types.ContainsKey(an)) continue;
            var encoded = BuildAttribute(a); if (encoded != null) set(encoded.Constructor, encoded.Blob);
        }
    }

    EncodedAttribute? TryAttribute(ConstructorInfo? ctor, Type[] parameterTypes, object[] args,
        NamedAttributeArg[] namedArgs, string attr)
    {
        try
        {
            if (ctor == null) throw new ArgumentException("attribute constructor was not found");
            if (parameterTypes.Length != args.Length) throw new ArgumentException("attribute argument count does not match constructor");
            return new EncodedAttribute(ctor, EncodeAttributeBlob(parameterTypes, args, namedArgs));
        }
        catch (Exception ex) when (ex is NotSupportedException || ex is ArgumentException)
        {
            Console.Error.WriteLine($"ilemit: skipping un-encodable custom attribute [{attr}]: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    void SetAttribute(Action<ConstructorInfo, byte[]> set, ConstructorInfo ctor, Type[] parameterTypes, params object[] args)
        => set(ctor, EncodeAttributeBlob(parameterTypes, args, Array.Empty<NamedAttributeArg>()));

    // II.23.3: prolog, fixed arguments in constructor order, then zero named arguments. Parameter Types come from the
    // same target universe as the constructor; only their ECMA element kind is inspected, never their host identity.
    static byte[] EncodeAttributeBlob(Type[] parameterTypes, object[] args, NamedAttributeArg[] namedArgs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)1);
        for (var i = 0; i < args.Length; i++) WriteAttributeValue(writer, parameterTypes[i], args[i]);
        writer.Write((ushort)namedArgs.Length);
        foreach (var named in namedArgs)
        {
            writer.Write(named.Field ? (byte)0x53 : (byte)0x54); // FIELD / PROPERTY
            WriteFieldOrPropType(writer, named.Type);
            WriteSerString(writer, named.Name);
            WriteAttributeValue(writer, named.Type, named.Value);
        }
        writer.Flush();
        return stream.ToArray();
    }

    // ECMA-335 II.23.3 FieldOrPropType. CIR states the named argument's declared type explicitly; ilemit encodes that
    // fact without reflecting over the attribute property or inferring a Kotlin annotation shape.
    static void WriteFieldOrPropType(BinaryWriter writer, Type type)
    {
        if (type.IsEnum)
        {
            writer.Write((byte)0x55);
            WriteSerString(writer, type.FullName);
            return;
        }
        if (type.IsArray)
        {
            writer.Write((byte)0x1d);
            WriteFieldOrPropType(writer, type.GetElementType());
            return;
        }
        var code = type.FullName switch
        {
            "System.Boolean" => 0x02,
            "System.Char" => 0x03,
            "System.SByte" => 0x04,
            "System.Byte" => 0x05,
            "System.Int16" => 0x06,
            "System.UInt16" => 0x07,
            "System.Int32" => 0x08,
            "System.UInt32" => 0x09,
            "System.Int64" => 0x0a,
            "System.UInt64" => 0x0b,
            "System.Single" => 0x0c,
            "System.Double" => 0x0d,
            "System.String" => 0x0e,
            "System.Type" => 0x50,
            "System.Object" => 0x51,
            _ => throw new NotSupportedException($"custom attribute named argument type '{type}' is not supported"),
        };
        writer.Write((byte)code);
    }

    static void WriteAttributeValue(BinaryWriter writer, Type type, object value)
    {
        if (type.IsEnum)
        {
            WriteAttributeValue(writer, type.GetEnumUnderlyingType(), value);
            return;
        }
        if (type.IsArray)
        {
            if (value == null) { writer.Write(-1); return; }
            if (value is not Array values) throw new ArgumentException($"custom attribute value is not an array for {type}");
            writer.Write(values.Length);
            foreach (var item in values) WriteAttributeValue(writer, type.GetElementType(), item);
            return;
        }

        switch (type.FullName)
        {
            case "System.Boolean": writer.Write(Convert.ToBoolean(value)); return;
            case "System.Char": writer.Write(Convert.ToUInt16(value)); return;
            case "System.SByte": writer.Write(Convert.ToSByte(value)); return;
            case "System.Byte": writer.Write(Convert.ToByte(value)); return;
            case "System.Int16": writer.Write(Convert.ToInt16(value)); return;
            case "System.UInt16": writer.Write(Convert.ToUInt16(value)); return;
            case "System.Int32": writer.Write(Convert.ToInt32(value)); return;
            case "System.UInt32": writer.Write(Convert.ToUInt32(value)); return;
            case "System.Int64": writer.Write(Convert.ToInt64(value)); return;
            case "System.UInt64": writer.Write(Convert.ToUInt64(value)); return;
            case "System.Single": writer.Write(Convert.ToSingle(value)); return;
            case "System.Double": writer.Write(Convert.ToDouble(value)); return;
            case "System.String": WriteSerString(writer, (string)value); return;
            case "System.Type":
                WriteSerString(writer, value is Type t ? t.AssemblyQualifiedName : (string)value);
                return;
            default:
                throw new NotSupportedException($"custom attribute fixed argument type '{type}' is not supported");
        }
    }

    static void WriteSerString(BinaryWriter writer, string value)
    {
        if (value == null) { writer.Write((byte)0xff); return; }
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteCompressedUInt(writer, (uint)bytes.Length);
        writer.Write(bytes);
    }

    static void WriteCompressedUInt(BinaryWriter writer, uint value)
    {
        if (value <= 0x7f) writer.Write((byte)value);
        else if (value <= 0x3fff)
        {
            writer.Write((byte)((value >> 8) | 0x80));
            writer.Write((byte)value);
        }
        else if (value <= 0x1fffffff)
        {
            writer.Write((byte)((value >> 24) | 0xc0));
            writer.Write((byte)(value >> 16));
            writer.Write((byte)(value >> 8));
            writer.Write((byte)value);
        }
        else throw new ArgumentException("custom attribute string is too long");
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
    // methods by type only, so the names are lost — and dll2klib falls back to arg0/arg1, which blocks named-argument
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
            // PARAMETER-level custom attributes — the generic BuildAttribute path. bir2cir (RoundtripMetadata) folds the
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
            if (vararg) SetAttribute(pb.SetCustomAttribute,
                // #370-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
                Bcl("System.ParamArrayAttribute").GetConstructor(Type.EmptyTypes), Array.Empty<Type>());
            if (hasDefault) { try { pb.SetConstant(ConstArgValue(dflt)); } catch { } }
            // Apply each param attribute whose type this assembly can encode (an in-assembly emitted type, or an
            // `attrExternal`-flagged referenced type); an attr referencing a type not in `_types` is skipped (BuildAttribute
            // would KeyNotFound) — the same "the CLR layer decides what is encodable" policy the method-level path uses.
            if (hasAttrs)
                foreach (var a in pattrs.EnumerateArray())
                {
                    var an = SlotName(a.GetProperty("attr"));   // structured `{t:fqn}` identity node (#48)
                    var anExternal = a.TryGetProperty("attrExternal", out var anExt) && anExt.GetBoolean();
                    if (!anExternal && !_types.ContainsKey(an)) continue;
                    var encoded = BuildAttribute(a); if (encoded != null) pb.SetCustomAttribute(encoded.Constructor, encoded.Blob);
                }
            i++;
        }
    }
}
