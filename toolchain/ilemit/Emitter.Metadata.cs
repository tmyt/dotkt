// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
#nullable enable annotations
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using DotKt.Bir;
using DotKt.Cir;

// Typed physical CIR metadata -> ECMA-335 custom-attribute blobs and parameter
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
    FieldBuilder DefineVolatileField(TypeBuilder tb, string name, Type type, FieldAttributes attrs,
        Type[] required, Type[] optional)
    {
        var modifiers = required.Append(Bcl("System.Runtime.CompilerServices.IsVolatile")).Distinct().ToArray();
        var fb = tb.DefineField(name, type, modifiers, optional, attrs);
        _volatileFields.Add(fb);
        return fb;
    }

    // Emit the `volatile.` prefix before a ld/st opcode when the field is volatile (no-op otherwise). Pairs with the
    // `modreq(IsVolatile)` on the field itself — this is what the C# compiler emits for a `volatile` field access.
    void MaybeVolatile(FieldInfo fld, bool carried = false)
    {
        var isVolatile = fld != null && _volatileFields.Contains(fld) || carried;
        if (isVolatile) _il.Emit(OpCodes.Volatile);
    }

    // Returns null when the CLR custom-attribute encoder cannot represent this annotation's shape (so the caller
    // skips it). kotc emits EVERY annotation verbatim (it is just metadata to the frontend); the CLR layer decides
    // what is encodable. The blob is encoded here instead of through CustomAttributeBuilder: CAB validates target-MLC
    // parameter Types against host-runtime argument Types, which would reintroduce the mixed-universe bug #336 removes.
    EncodedAttribute? BuildAttribute(AttributeApplication a)
    {
        var attr = (a.Attr as TypeNode.Fqn)?.Name ?? throw new InvalidOperationException("Invalid attribute type");
        var args = a.Args.Select(ConstArgValue).ToArray();
        var namedArgs = a.NamedArgs is { } named
            ? named.Select(item => new NamedAttributeArg(
                item.Kind == "field", item.Name, MapType(item.Type), ConstArgValue(item.Value))).ToArray()
            : Array.Empty<NamedAttributeArg>();
        if (a.AttrExternal == true)
        {
            var argTypes = a.ArgTypes.Select(MapType).ToArray();
            if (PrimaryFromRef(a.MemberRef) is not ConstructorInfo constructor)
                throw new InvalidOperationException($"Applied attribute [{attr}] has no resolved constructor");
            return TryAttribute(constructor, argTypes, args, namedArgs, attr);
        }
        if (!_types.ContainsKey(attr))
        {
            Console.Error.WriteLine($"ilemit: skipping custom attribute [{attr}]: type not emitted in this assembly");
            return null;
        }
        var ti = _types[attr];
        EnsureCtorsDefined(ti);
        var declaredArgTypes = a.ArgTypes.Select(SigCanon).ToArray();
        var hits = new List<(ConstructorInfo ctor, Type[] parameterTypes)>();
        for (int i = 0; i < ti.Ctors.Count; i++)
        {
            var ps = ti.CtorDefs[i].Params;
            if (ps.Length != declaredArgTypes.Length) continue;
            var parameterNodes = ps.Select(p => p.Type).ToArray();
            if (!parameterNodes.Select(SigCanon).SequenceEqual(declaredArgTypes)) continue;
            hits.Add((ti.Ctors[i], parameterNodes.Select(MapType).ToArray()));
        }
        if (hits.Count == 0)
            return TryAttribute(null, Array.Empty<Type>(), args, namedArgs, attr);
        if (hits.Count > 1)
            throw new InvalidOperationException($"Attribute constructor {attr}[{string.Join(",", declaredArgTypes)}] links {hits.Count} declarations");
        return TryAttribute(hits[0].ctor, hits[0].parameterTypes, args, namedArgs, attr);
    }

    // Stamp each CIR `attrs` entry of a field/property/return-parameter decl onto its builder — the SAME generic
    // BuildAttribute path the type/method/param sites use. Skips an attr whose type is neither `attrExternal`-flagged nor
    // emitted in this assembly (BuildAttribute would KeyNotFound) — the CLR layer decides what is encodable. bir2cir (RoundtripMetadata)
    // folds the round-trip metadata ([KotlinReadOnly]/[KotlinSuspendFunctionType]/[Nullable]/…) into these arrays;
    // ilemit only STAMPS. `set` is FieldBuilder/PropertyBuilder/ParameterBuilder.SetCustomAttribute.
    void StampMemberAttrs(Action<ConstructorInfo, byte[]> set, OptionalField<AttributeApplication[]> attributes)
    {
        foreach (var a in attributes.OrEmpty())
        {
            var an = (a.Attr as TypeNode.Fqn)?.Name;
            if (a.AttrExternal != true && !_types.ContainsKey(an)) continue;
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

    // DllImportAttribute is a pseudo-custom attribute: Reflection.Emit converts this exact blob into MethodDef
    // PinvokeImpl/PreserveSig flags plus an ImplMap row and does not retain a CustomAttribute row. bir2cir has already
    // normalized every Kotlin/source choice into the CIR descriptor, including defaults; this method only maps each
    // stated physical field to the equivalent metadata API input.
    void ApplyPInvokeDescriptor(MethodBuilder method, CirPInvoke descriptor)
    {
        var ctor = RequiredRef<ConstructorInfo>(descriptor.AttributeCtorRef, "a P/Invoke descriptor");
        var stringType = Bcl("System.String");
        var booleanType = Bcl("System.Boolean");
        var callingConventionType = MapType(descriptor.CallingConventionType);
        var charSetType = MapType(descriptor.CharSetType);
        var callingConvention = descriptor.CallingConvention switch
        {
            "winapi" => 1,
            "cdecl" => 2,
            "stdcall" => 3,
            "thiscall" => 4,
            "fastcall" => 5,
            var value => throw new InvalidOperationException($"ilemit: malformed P/Invoke calling convention '{value}'"),
        };
        var charSet = descriptor.CharSet switch
        {
            "none" => 1,
            "ansi" => 2,
            "unicode" => 3,
            "auto" => 4,
            var value => throw new InvalidOperationException($"ilemit: malformed P/Invoke character set '{value}'"),
        };
        var preserveSig = descriptor.PreserveSig;
        var named = new List<NamedAttributeArg>();
        foreach (var field in descriptor.PseudoFields)
            named.Add(field switch
            {
                "EntryPoint" => new(true, "EntryPoint", stringType, descriptor.EntryPoint),
                "CallingConvention" => new(true, "CallingConvention", callingConventionType, callingConvention),
                "CharSet" => new(true, "CharSet", charSetType, charSet),
                "ExactSpelling" => new(true, "ExactSpelling", booleanType, descriptor.ExactSpelling),
                "SetLastError" => new(true, "SetLastError", booleanType, descriptor.SetLastError),
                "PreserveSig" => new(true, "PreserveSig", booleanType, preserveSig),
                "BestFitMapping" => new(true, "BestFitMapping", booleanType, descriptor.BestFitMapping),
                "ThrowOnUnmappableChar" => new(true, "ThrowOnUnmappableChar", booleanType, descriptor.ThrowOnUnmappableChar),
                var value => throw new InvalidOperationException($"ilemit: malformed P/Invoke named field '{value}'"),
            });
        method.SetCustomAttribute(ctor, EncodeAttributeBlob(
            new[] { stringType }, new object[] { descriptor.Module }, named.ToArray()));

        // Keep PreserveSig exact even if Reflection.Emit's pseudo-attribute implementation changes its default.
        var implementationFlags = method.GetMethodImplementationFlags();
        method.SetImplementationFlags(preserveSig
            ? implementationFlags | MethodImplAttributes.PreserveSig
            : implementationFlags & ~MethodImplAttributes.PreserveSig);
    }

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
        if (IsAttributeEnumType(type))
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
        if (IsAttributeEnumType(type))
        {
            // bir2cir has already resolved an enum constant to its exact physical primitive. In particular, a local
            // enum can still be represented by Reflection.Emit as a TypeBuilder here, whose IsEnum/
            // GetEnumUnderlyingType surface is unavailable before final assembly creation. Encode the stated primitive
            // directly; ilemit neither recovers the declaration nor infers a value from an entry name or ordinal.
            switch (value)
            {
                case sbyte v: writer.Write(v); return;
                case byte v: writer.Write(v); return;
                case short v: writer.Write(v); return;
                case ushort v: writer.Write(v); return;
                case int v: writer.Write(v); return;
                case uint v: writer.Write(v); return;
                case long v: writer.Write(v); return;
                case ulong v: writer.Write(v); return;
                default: throw new ArgumentException($"custom attribute enum value '{value}' has no integral physical representation");
            }
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
            case "System.Object" when value is Type token:
                writer.Write((byte)0x50); // Boxed CLASS token, ECMA-335 II.23.3.
                WriteSerString(writer, token.AssemblyQualifiedName);
                return;
            default:
                throw new NotSupportedException($"custom attribute fixed argument type '{type}' is not supported");
        }
    }

    static bool IsAttributeEnumType(Type type) =>
        type.IsEnum || type is TypeBuilder && type.BaseType?.FullName == "System.Enum";

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

    object ConstArgValue(AttributeArgument value) => value switch
    {
        AttributeArgument.NullValue => null,
        AttributeArgument.UnencodableValue => null,
        AttributeArgument.BooleanValue v => v.Value,
        AttributeArgument.CharValue v => (char)v.Value,
        AttributeArgument.Int8Value v => v.Value,
        AttributeArgument.UInt8Value v => v.Value,
        AttributeArgument.Int16Value v => v.Value,
        AttributeArgument.UInt16Value v => v.Value,
        AttributeArgument.Int32Value v => v.Value,
        AttributeArgument.UInt32Value v => v.Value,
        AttributeArgument.Int64Value v => v.Value,
        AttributeArgument.UInt64Value v => v.Value,
        AttributeArgument.Float32Value v => BitConverter.Int32BitsToSingle(v.Bits),
        AttributeArgument.Float64Value v => BitConverter.Int64BitsToDouble(v.Bits),
        AttributeArgument.TextValue v => v.Value,
        AttributeArgument.Utf16Value v => new string(v.Units.Select(unit => (char)unit).ToArray()),
        AttributeArgument.BytesValue v => v.Bytes,
        AttributeArgument.TypeValue v => MapType(v.Type),
        AttributeArgument.ArrayValue v => v.Elems.Select(ConstArgValue).ToArray(),
        AttributeArgument.EnumValue { PhysicalValue: { } physical, Underlying: { } underlying } =>
            EnumConstantValue(underlying, physical),
        AttributeArgument.EnumValue v => v.Ordinal ?? throw new InvalidOperationException("Invalid enum constant"),
        _ => throw new InvalidOperationException("Invalid metadata value"),
    };

    // Emit parameter NAMES into the metadata (DefineParameter is 1-based; 0 = return). ilemit otherwise defines
    // methods by type only, so the names are lost — and dll2klib falls back to arg0/arg1, which blocks named-argument
    // calls across an assembly boundary. The names come straight from the BIR params.
    void DefineParamNames(MethodBuilder mb, ICirCallable m) => DefineParamNames(mb.DefineParameter, m.Params);
    void DefineParamNames(ConstructorBuilder cb, ICirCallable m) => DefineParamNames(cb.DefineParameter, m.Params);
    void DefineParamNames(Func<int, ParameterAttributes, string, ParameterBuilder> defineParam, CirParameter[] parameters)
    {
        int i = 1;
        foreach (var p in parameters)
        {
            var name = p.Name.OrDefault() ?? "";
            bool vararg = p.Mods.OrDefault()?.Vararg.OrDefault() == true;
            bool hasDefault = p.Default.IsPresent;
            // PARAMETER-level custom attributes — the generic BuildAttribute path. bir2cir (RoundtripMetadata) folds the
            // round-trip metadata into this array too ([Nullable] for a nullable-reference param, [KotlinSuspendFunctionType]
            // for a suspend fn-type param), so the parameter builder must be forced whenever attrs are present even if the
            // param otherwise carries no name/vararg/default. ilemit only STAMPS — no Kotlin-semantic decision here.
            var pattrs = p.Attrs.OrEmpty();
            bool hasAttrs = pattrs.Length > 0;
            if (name.Length == 0 && !vararg && !hasDefault && !hasAttrs) { i++; continue; }
            // A constant default -> [Optional] + DefaultParameterValue, so a cross-module caller can omit the arg.
            var attrs = hasDefault ? ParameterAttributes.Optional | ParameterAttributes.HasDefault : ParameterAttributes.None;
            var pb = defineParam(i, attrs, name.Length > 0 ? name : null);
            // `vararg xs: T` -> [ParamArray] so the .NET signature is a params array (a C# OR Kotlin consumer can spread).
            if (vararg) SetAttribute(pb.SetCustomAttribute,
                // member-lookup-residual: metadata the output format obliges: an attribute the emitter stamps to DESCRIBE the assembly, not a call any program makes
                Bcl("System.ParamArrayAttribute").GetConstructor(Type.EmptyTypes), Array.Empty<Type>());
            if (hasDefault) pb.SetConstant(ConstArgValue(p.Default.Value));
            // Apply each param attribute whose type this assembly can encode (an in-assembly emitted type, or an
            // `attrExternal`-flagged referenced type); an attr referencing a type not in `_types` is skipped (BuildAttribute
            // would KeyNotFound) — the same "the CLR layer decides what is encodable" policy the method-level path uses.
            if (hasAttrs)
                foreach (var a in pattrs)
                {
                    var an = (a.Attr as TypeNode.Fqn)?.Name;
                    if (a.AttrExternal != true && !_types.ContainsKey(an)) continue;
                    var encoded = BuildAttribute(a); if (encoded != null) pb.SetCustomAttribute(encoded.Constructor, encoded.Blob);
                }
            i++;
        }
    }
}
