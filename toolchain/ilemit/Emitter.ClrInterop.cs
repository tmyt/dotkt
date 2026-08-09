// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Globalization;

// CLR interop emission: @Clr native calls, property/event access, ctor picking, BCL-intrinsic handlers.
sealed partial class Emitter
{
    static string ResolvedOwnerIdentity(JsonElement holder, string descriptor, string context)
    {
        if (!holder.TryGetProperty(descriptor, out var ownerEl) || ownerEl.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"ilemit: {context} is missing its resolved `{descriptor}` type descriptor");
        DotKt.Bir.TypeNode owner;
        try { owner = DotKt.Bir.TypeNode.Read(ownerEl); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"ilemit: {context} has a malformed `{descriptor}` type descriptor", ex);
        }
        if (owner is not DotKt.Bir.TypeNode.Fqn fqn || fqn.Args is { Length: > 0 })
            throw new InvalidOperationException($"ilemit: {context} has a non-scalar `{descriptor}` type descriptor: {ownerEl}");
        return fqn.Name;
    }

    Type EmitNativeClrSafeCastValue(JsonElement e)
    {
        // `x as? T` for value T -> `T?`: isinst boxed-T, then unbox+wrap, else empty Nullable<T>.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var res = _il.DeclareLocal(nt);
        var has = _il.DefineLabel();
        var done = _il.DefineLabel();
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Isinst, elem);
        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Brtrue, has);
        _il.Emit(OpCodes.Pop);
        _il.Emit(OpCodes.Ldloca, res);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, res);
        _il.Emit(OpCodes.Br, done);
        _il.MarkLabel(has);
        _il.Emit(OpCodes.Unbox_Any, elem);
        EmitConstructor(_il, OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
        _il.MarkLabel(done);
        return nt;
    }

    Type EmitNativeClrNullableNull(JsonElement e)
    {
        // `null` typed as Int? -> a Nullable<T> with HasValue=false. NOT ldnull: a value type has no null reference.
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Ldloca, loc);
        _il.Emit(OpCodes.Initobj, nt);
        _il.Emit(OpCodes.Ldloc, loc);
        return nt;
    }

    Type EmitNativeClrNullableWrap(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        EmitExpr(e.GetProperty("e"));
        EmitConstructor(_il, OpCodes.Newobj, nt.GetConstructor(new[] { elem }));
        return nt;
    }

    Type EmitNativeClrNullableHasValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        EmitExpr(e.GetProperty("e"));
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        EmitMethod(_il, OpCodes.Call, nt.GetProperty("HasValue").GetGetMethod());
        return Bcl("System.Boolean");
    }

    Type EmitNativeClrNullableValue(JsonElement e)
    {
        var elem = NativeType(e.GetProperty("elem"));
        var nt = ConstructedType(Bcl("System.Nullable`1"), elem);
        var src = EmitExpr(e.GetProperty("e"));
        // REDUNDANT unwrap: the source already holds the non-nullable `elem`, not a `Nullable<elem>`. The 2.4.0
        // frontend emits a nested `nullableValue{ nullableValue{ x } }` for a safe-call member access, so the outer
        // node lands on an already-unwrapped value. `stloc` of a raw `elem` into a `Nullable<elem>` local would
        // REINTERPRET its bytes as the {HasValue,value} struct layout -> `.Value` reads garbage (a Char 'x' -> 0).
        // The stack already holds `elem` -- nothing to do.
        if (src == elem) return elem;
        // Any source that is neither the unwrapped `elem` nor the `Nullable<elem>` we expect would be
        // silently byte-reinterpreted by the `stloc` below. Fail loud so a future frontend shape surfaces
        // at emit time rather than as another garbage value.
        if (src != null && src != nt)
            throw new NotSupportedException($"nullableValue: source {src} is neither {elem} nor {nt}");
        var loc = _il.DeclareLocal(nt);
        _il.Emit(OpCodes.Stloc, loc);
        _il.Emit(OpCodes.Ldloca, loc);
        EmitMethod(_il, OpCodes.Call, nt.GetProperty("Value").GetGetMethod());
        return elem;
    }

    Type EmitNativeClrTypeOf(JsonElement e)
    {
        var t = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, t);
        EmitMethod(_il, OpCodes.Call, Bcl("System.Type").GetMethod("GetTypeFromHandle"));
        return Bcl("System.Type");
    }

    Type EmitNativeClrGetType(JsonElement e)
    {
        var got = EmitExpr(e.GetProperty("e"));
        if (got != null && NeedsBoxToRef(got)) _il.Emit(OpCodes.Box, got);
        EmitMethod(_il, OpCodes.Callvirt, Bcl("System.Object").GetMethod("GetType"));
        return Bcl("System.Type");
    }

    Type EmitNativeClrEnumValue(JsonElement e)
    {
        if (e.TryGetProperty("physicalValue", out var pv)
            && e.TryGetProperty("underlying", out var ut))
        {
            var text = pv.GetString();
            switch (ut.GetString())
            {
                case "System.Int64":
                    _il.Emit(OpCodes.Ldc_I8, long.Parse(text, CultureInfo.InvariantCulture));
                    break;
                case "System.UInt64":
                    _il.Emit(OpCodes.Ldc_I8, unchecked((long)ulong.Parse(text, CultureInfo.InvariantCulture)));
                    break;
                case "System.UInt32":
                    _il.Emit(OpCodes.Ldc_I4, unchecked((int)uint.Parse(text, CultureInfo.InvariantCulture)));
                    break;
                case "System.Byte":
                case "System.UInt16":
                    _il.Emit(OpCodes.Ldc_I4, int.Parse(text, CultureInfo.InvariantCulture));
                    break;
                default:
                    _il.Emit(OpCodes.Ldc_I4, int.Parse(text, CultureInfo.InvariantCulture));
                    break;
            }
        }
        else
        {
            // A locally-declared Kotlin basic enum has contiguous 0..N values by construction.
            _il.Emit(OpCodes.Ldc_I4, e.GetProperty("ordinal").GetInt32());
        }
        return NativeType(e.GetProperty("type"));
    }

    Type EmitNativeClrEnumOrdinal(JsonElement e)
    {
        // A LOCAL value-type enum's ordinal == its underlying value (kotc assigns contiguous 0..n) -> a plain Conv_I4.
        // A REFERENCED .NET enum (#107 — the node carries the enum `type`) may have sparse/negative/aliased values, so
        // its Kotlin ordinal (the DECLARATION INDEX) is Array.IndexOf(Enum.GetValues(t), value), NOT the underlying int.
        if (e.TryGetProperty("type", out var tp))
        {
            var et = NativeType(tp);
            _il.Emit(OpCodes.Ldtoken, et);
            EmitMethod(_il, OpCodes.Call, Bcl("System.Type").GetMethod("GetTypeFromHandle"));
            EmitMethod(_il, OpCodes.Call, Bcl("System.Enum").GetMethod("GetValues", new[] { Bcl("System.Type") }));
            EmitExpr(e.GetProperty("e"));
            _il.Emit(OpCodes.Box, et);
            EmitMethod(_il, OpCodes.Call, Bcl("System.Array").GetMethod("IndexOf", new[] { Bcl("System.Array"), Bcl("System.Object") }));
            return Bcl("System.Int32");
        }
        EmitExpr(e.GetProperty("e"));
        _il.Emit(OpCodes.Conv_I4);
        return Bcl("System.Int32");
    }

    Type EmitNativeClrEnumValues(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        EmitMethod(_il, OpCodes.Call, Bcl("System.Type").GetMethod("GetTypeFromHandle"));
        EmitMethod(_il, OpCodes.Call, Bcl("System.Enum").GetMethod("GetValues", new[] { Bcl("System.Type") }));
        _il.Emit(OpCodes.Castclass, et.MakeArrayType());
        return et.MakeArrayType();
    }

    Type EmitNativeClrEnumParse(JsonElement e)
    {
        var et = NativeType(e.GetProperty("type"));
        _il.Emit(OpCodes.Ldtoken, et);
        EmitMethod(_il, OpCodes.Call, Bcl("System.Type").GetMethod("GetTypeFromHandle"));
        EmitExpr(e.GetProperty("arg"));
        EmitMethod(_il, OpCodes.Call, Bcl("System.Enum").GetMethod("Parse", new[] { Bcl("System.Type"), Bcl("System.String") }));
        _il.Emit(OpCodes.Unbox_Any, et);
        return et;
    }

    Type EmitClrNew(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var args = e.GetProperty("args");
        // W1-S2 (#46) CONSUME-ONLY: bir2cir already RESOLVED the ctor overload against the ref.dll MLC and stamped the
        // winning ctor's DECLARED param signature as `memberSig`; link the UNIQUE matching constructor (0 = hard ABI
        // error, >1 = malformed) — no exact-then-assignability-then-arity cascade, no delegate-arity scoring.
        var openCtor = LinkClrCtor(type, e, out var tb);
        if (tb)
        {
            // A generic collection constructed with an EMITTED element type (`new HashSet<EmittedType>()`) is a
            // TypeBuilderInstantiation whose members can't be reflected — the winner was matched on the OPEN def. Emit
            // the args against the SUBSTITUTED param types (so a delegate/closure arg's rewrap target is the CLOSED
            // param `Func<Box>`, not the open `Func<T>`), then re-anchor the ctor.
            var classArgs = type.GetGenericArguments();
            var openPs = openCtor.GetParameters();
            int ai = 0;
            foreach (var a in args.EnumerateArray()) { EmitArg(a, SubstituteIfaceArgs(openPs[ai].ParameterType, classArgs)); ai++; }
            RequireArgCount(ai, openPs.Length, openCtor.ToString());
            EmitConstructor(_il, OpCodes.Newobj, AnchorConstructor(type, openCtor));
            return type;
        }
        EmitArgs(args, openCtor.GetParameters());
        EmitConstructor(_il, OpCodes.Newobj, openCtor);
        return type;
    }

    // W1-S2 (#46) CONSUME-ONLY ctor linking. bir2cir stamped the resolved ctor's DECLARED param signature as `memberSig`
    // (a class type-var as a positional `tv(type,i)`). Enumerate the type's ctors — on the OPEN def when the constructed
    // type is a TypeBuilderInstantiation (`tb`, caller re-anchors) — and match each declared param STRUCTURALLY under
    // positional-tv equality (GenericParamMatches, shared with the S1 generic matcher). Require EXACTLY ONE: 0 is a hard
    // ABI-mismatch error, >1 a malformed-descriptor error, each printing the full descriptor.
    ConstructorInfo LinkClrCtor(Type type, JsonElement e, out bool tb, string descriptorName = "memberSig",
        bool includeNonPublic = false)
    {
        if (!e.TryGetProperty(descriptorName, out var sigEl) || sigEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"ilemit: constructor {type?.FullName} is missing its `{descriptorName}` descriptor (bir2cir must carry the resolved physical ctor signature — W1-S2 #46)");
        var declParams = sigEl.EnumerateArray().Select(DotKt.Bir.TypeNode.Read).ToArray();
        var ownerDescriptor = descriptorName == "baseMemberSig" ? "baseMemberOwner" : "memberOwner";
        var declaredOwner = ResolvedOwnerIdentity(e, ownerDescriptor, $"constructor {type?.FullName}");
        tb = IsTbInstantiation(type);
        var searchType = tb ? type.GetGenericTypeDefinition() : type;
        // A CONSTRUCTED reflection owner substitutes its class type-vars on each ctor param (`IEnumerable<Int32>`) —
        // resolve a `tv(type,i)` memberSig entry against those args; the OPEN-def / TbInstantiation path keeps `tv`
        // positional (ownerArgs null). Mirrors ResolveGenericMethod's ownerArgs branch.
        Type[] ownerArgs = tb ? null : (type.IsGenericType ? type.GetGenericArguments() : null);
        var flags = BindingFlags.Public | BindingFlags.Instance
            | (includeNonPublic ? BindingFlags.NonPublic : 0);
        var cands = searchType.GetConstructors(flags)
            .Where(c => DeclaringTypeIdentity(c) == declaredOwner && c.GetParameters().Length == declParams.Length).ToList();
        var hits = cands.Where(c => c.GetParameters()
            .Select((p, i) => GenericParamMatches(declParams[i], p.ParameterType, ownerArgs)).All(x => x)).ToList();
        var desc = $"{type?.FullName}::.ctor{sigEl}";
        if (hits.Count == 1) return hits[0];
        if (hits.Count == 0)
            throw new InvalidOperationException($"ilemit: no constructor matches the resolved descriptor {desc} (ABI mismatch; {cands.Count} same-arity candidate(s): {string.Join("; ", cands.Select(c => c.ToString()))})");
        throw new InvalidOperationException($"ilemit: resolved ctor descriptor {desc} is AMBIGUOUS — {hits.Count} constructors match (malformed memberSig): {string.Join("; ", hits.Select(c => c.ToString()))}");
    }

    // W1-S2 (#46) CONSUME-ONLY method linking. bir2cir stamped the resolved member's DECLARED param signature as
    // `memberSig` (positional-tv for a generic owner/method). Enumerate the owner's name + static/instance + param-count
    // candidates (incl. inherited class members via GetMethods, and base-interface members for an interface owner) — on
    // the OPEN def when the owner is a TypeBuilderInstantiation (re-anchored via TypeBuilder.GetMethod) — and match each
    // declared param STRUCTURALLY under positional-tv equality (GenericParamMatches). Require EXACTLY ONE hit: 0 is a
    // hard ABI-mismatch error, >1 a malformed-descriptor error, each printing the full descriptor. No arity probe, no
    // name+arity first-pick, no assignability scoring, no dynamic-dispatch/Bcl("System.Object") degradation.
    MethodInfo LinkClrMethod(Type type, string name, JsonElement e, bool instance)
    {
        if (!e.TryGetProperty("memberSig", out var sigEl) || sigEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"ilemit: clr{(instance ? "Instance" : "Static")} call to {type?.FullName}.{name} is missing its `memberSig` descriptor (bir2cir must carry the FIR-resolved parameter signature — W1-S2 #46)");
        var declParams = sigEl.EnumerateArray().Select(DotKt.Bir.TypeNode.Read).ToArray();
        var declaredOwner = ResolvedOwnerIdentity(e, "memberOwner", $"call to {type?.FullName}.{name}");
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
            (instance ? BindingFlags.Instance : BindingFlags.Static);
        Type[] ownerArgs = type.IsGenericType ? type.GetGenericArguments() : null;
        // A TypeBuilderInstantiation (constructed over an EMITTED arg — `IEnumerator<T>`, T a user class) can't reflect
        // its members; resolve on the OPEN def and re-anchor the winner via TypeBuilder.GetMethod. Its type-args are
        // erased for matching (ownerArgs null -> memberSig `tv` stays positional).
        bool reanchor = IsTbInstantiation(type);
        var searchType = reanchor ? type.GetGenericTypeDefinition() : type;
        if (reanchor) ownerArgs = null;
        // WITHOUT `typeArgs`, exclude generic-method DEFINITIONS so `Task.fromException` binds the non-generic
        // `Task FromException(Exception)`, not `Task<T> FromException<T>`; WITH `typeArgs` keep BOTH — a generic Kotlin
        // @ClrIntrinsic (`arrayCopy<T>`) can bind a NON-generic BCL method (`Array.Copy`), the structural match
        // disambiguates (mirrors bir2cir ClrMemberResolution's candidate filter).
        bool hasTypeArgs = e.TryGetProperty("typeArgs", out var taEl) && taEl.ValueKind == JsonValueKind.Array && taEl.GetArrayLength() > 0;
        List<MethodInfo> Match(IEnumerable<MethodInfo> cs) => cs.Where(m => DeclaringTypeIdentity(m) == declaredOwner
            && (hasTypeArgs || !m.IsGenericMethodDefinition)
            && m.GetParameters().Select((p, i) => GenericParamMatches(declParams[i], p.ParameterType, ownerArgs)).All(x => x))
            .GroupBy(m => (m.Module, m.MetadataToken)).Select(g => g.First()).ToList();
        MethodInfo[] Named(Type t) { try { return t.GetMethods(flags).Where(m =>
            IsPublicOrProtected(m) && m.Name == name && m.GetParameters().Length == declParams.Length).ToArray(); }
            catch { return Array.Empty<MethodInfo>(); } }
        var own = Named(searchType);
        var all = own.AsEnumerable();
        if (instance) all = all.Concat(SafeInterfaces(searchType).SelectMany(Named));
        var hits = Match(all);
        var desc = $"{type?.FullName}.{name}{sigEl}";
        // A TypeBuilderInstantiation owner re-anchors the winner via TypeBuilder.GetMethod — but that REQUIRES the winner's
        // declaring type to be the owner's open def. An INHERITED (base-class) member reflected off the open def declares on
        // the base, so GetMethod throws ArgumentException; the reflected handle is already a usable closed token (mirrors the
        // deleted ExternalPropAccessor's `catch (ArgumentException) { return mb; }`, which the S3 field/prop axes now share).
        if (hits.Count == 1)
        {
            var hit = ExactDeclaringMethod(hits[0]);
            // Re-anchor only a member DECLARED by this generic owner. An inherited slot already carries its own
            // declaring interface; anchoring `IEnumerator.MoveNext` onto `IEnumerator<T>` manufactures a member that
            // does not exist. (TypeBuilder.GetMethod used to reject that pair; SignatureMethod must preserve the same
            // boundary explicitly because it is a metadata description and therefore cannot validate it for us.)
            if (!reanchor) return hit;
            try
            {
                if (TypeIdentity(type) == declaredOwner) return AnchorMethod(type, hit);
                // An inherited slot must be anchored on its OWN instantiated declaration, not on the derived
                // receiver interface. Substitute the receiver's already-known arguments through the reflected base
                // edge (`IDictionary<K,V>` -> `ICollection<KeyValuePair<K,V>>`) and preserve memberOwner's exact slot.
                // This is mechanical projection of the selected declaration, not another member search.
                if (hit.DeclaringType is { IsConstructedGenericType: true } inheritedOwner)
                    return AnchorMethod(SubstituteIfaceArgs(inheritedOwner, type.GetGenericArguments()), hit);
                return hit;
            }
            catch (ArgumentException) { return hit; }
        }
        if (hits.Count == 0)
            throw new InvalidOperationException($"ilemit: no {(instance ? "instance" : "static")} .NET method matches the resolved descriptor {desc} (ABI mismatch; {own.Length} same-name/arity candidate(s): {string.Join("; ", own.Select(m => m.ToString()))})");
        throw new InvalidOperationException($"ilemit: resolved descriptor {desc} is AMBIGUOUS — {hits.Count} methods match (malformed memberSig): {string.Join("; ", hits.Select(m => m.ToString()))}");
    }

    static string DeclaringTypeIdentity(MethodBase member)
    {
        var declaring = member?.DeclaringType
            ?? throw new InvalidOperationException($"ilemit: member '{member}' has no declaring type");
        return TypeIdentity(declaring);
    }

    static bool IsPublicOrProtected(MethodBase member) =>
        member.IsPublic || member.IsFamily || member.IsFamilyOrAssembly;

    static string TypeIdentity(Type declaring)
    {
        try { if (declaring.IsGenericType && !declaring.IsGenericTypeDefinition) declaring = declaring.GetGenericTypeDefinition(); }
        catch { }
        return declaring.FullName ?? declaring.Name;
    }

    static Type[] SafeInterfaces(Type t) { try { return t.GetInterfaces(); } catch { return Array.Empty<Type>(); } }

    // GetMethods on a constructed interface includes inherited members whose DeclaringType is a different interface.
    // PersistedAssemblyBuilder may nevertheless encode such a reflected handle against its ReflectedType: asking
    // `IEnumerator<T>` for the inherited non-generic `IEnumerator.MoveNext` then writes the impossible MemberRef
    // `IEnumerator<T>.MoveNext`. bir2cir's memberOwner has already selected the declaration. Re-fetch the SAME metadata
    // token from that declaration type so emission preserves that scalar fact; this performs no lookup or selection.
    static MethodInfo ExactDeclaringMethod(MethodInfo method)
    {
        var declaring = method.DeclaringType;
        if (declaring == null) return method;
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            return declaring.GetMethods(flags)
                .FirstOrDefault(candidate => candidate.Module == method.Module
                    && candidate.MetadataToken == method.MetadataToken) ?? method;
        }
        catch { return method; }
    }

    // W1-S4 (#46/#183) CONSUME-ONLY declaration-side override linking. A method overriding a .NET base-CLASS virtual
    // (accessor) carries `clrOverride` (the base owner FQN) + `clrOverrideSig` (bir2cir's ref.dll-resolved base-virtual
    // param signature, positional-tv for a generic base). This LINKS the exact base slot for DefineMethodOverride —
    // enumerate the base type's name + instance + virtual + param-count candidates, match each param STRUCTURALLY under
    // positional-tv equality (GenericParamMatches, ownerArgs null -> memberSig `tv` stays positional against the OPEN
    // base def), require EXACTLY ONE (0 = hard ABI error, >1 = malformed). Replaces the former
    // `baseT.GetMethod(name, ps) ?? baseT.GetMethod(name)` NAME-ONLY first-pick fallback. A generic base's winner is
    // re-anchored onto the emitted type's CONSTRUCTED base instantiation (DefineMethodOverride must reference the slot
    // on `Collection<Item>`, not the open def) — the corpus exercises only the non-generic accessor case.
    MethodInfo LinkOverrideBase(Type baseT, string name, JsonElement m, Type derivedTb)
    {
        if (!m.TryGetProperty("clrOverrideSig", out var sigEl) || sigEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"ilemit: override of {baseT?.FullName}.{name} is missing its `clrOverrideSig` descriptor (bir2cir must carry the resolved base-virtual signature — W1-S4 #46/#183)");
        var declParams = sigEl.EnumerateArray().Select(DotKt.Bir.TypeNode.Read).ToArray();
        var declaredOwner = ResolvedOwnerIdentity(m, "clrOverrideOwner", $"override of {baseT?.FullName}.{name}");
        var searchType = baseT.IsGenericType && !baseT.IsGenericTypeDefinition ? baseT.GetGenericTypeDefinition() : baseT;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        MethodInfo[] named;
        try { named = searchType.GetMethods(flags).Where(x => x.Name == name && x.IsVirtual && x.GetParameters().Length == declParams.Length).ToArray(); }
        catch { named = Array.Empty<MethodInfo>(); }
        var hits = named.Where(x => DeclaringTypeIdentity(x) == declaredOwner && x.GetParameters()
            .Select((p, i) => GenericParamMatches(declParams[i], p.ParameterType, null)).All(y => y))
            .GroupBy(x => (x.Module, x.MetadataToken)).Select(g => g.First()).ToList();
        var desc = $"{baseT?.FullName}.{name}{sigEl}";
        if (hits.Count == 0)
            throw new InvalidOperationException($"ilemit: no base virtual matches the override descriptor {desc} (ABI mismatch; {named.Length} same-name/arity virtual(s): {string.Join("; ", named.Select(x => x.ToString()))})");
        if (hits.Count > 1)
            throw new InvalidOperationException($"ilemit: override descriptor {desc} is AMBIGUOUS — {hits.Count} base virtuals match (malformed clrOverrideSig): {string.Join("; ", hits.Select(x => x.ToString()))}");
        var win = hits[0];
        // Non-generic declaring base (the accessor case): the reflected slot is a usable closed handle.
        if (!(win.DeclaringType?.IsGenericType ?? false)) return win;
        // Generic .NET base: DefineMethodOverride must reference the slot on the emitted type's CONSTRUCTED base
        // instantiation (`Collection<Item>::InsertItem`). Walk the emitted type's base-CLASS chain for the instantiation
        // whose generic def is the winner's declaring type; TypeBuilder.GetMethod re-anchors a TypeBuilderInstantiation
        // base (emitted arg), else find the closed reflected slot by the shared def token.
        for (var bt = derivedTb?.BaseType; bt != null; bt = bt.BaseType)
            if (bt.IsGenericType && !bt.IsGenericTypeDefinition
                && ReferenceEquals(bt.GetGenericTypeDefinition(), win.DeclaringType.GetGenericTypeDefinition()))
            {
                try { return AnchorMethod(bt, win); }
                catch (ArgumentException)
                {
                    var closed = bt.GetMethods(flags).FirstOrDefault(x => x.Module == win.Module && x.MetadataToken == win.MetadataToken);
                    return closed ?? win;
                }
            }
        return win;
    }

    // The bir2cir clrInstance `dispatch` decision (call | callvirt | constrained) -> the IL opcode. bir2cir computed it
    // from the resolved MethodInfo IsVirtual/IsFinal + owner value-type-ness + the `super` base-slot flag (issue #14);
    // ilemit no longer re-derives it. `constrained.` prefixes the callvirt with the receiver type (== the node `type`).
    void EmitClrDispatch(MethodInfo mi, string dispatch, Type recvType)
    {
        switch (dispatch)
        {
            case "call": EmitMethod(_il, OpCodes.Call, mi); break;
            case "callvirt": EmitMethod(_il, OpCodes.Callvirt, mi); break;
            case "constrained": _il.Emit(OpCodes.Constrained, recvType); EmitMethod(_il, OpCodes.Callvirt, mi); break;
            default: throw new NotSupportedException($"ilemit: unknown clrInstance dispatch '{dispatch}' on {mi.DeclaringType}.{mi.Name} (bir2cir must emit call|callvirt|constrained — W1-S2 #46)");
        }
    }

    Type EmitClrCall(JsonElement e, bool instance, bool deref = true)
    {
        // `ClrRef` (not `ResolveType`) so a method on a constructed generic .NET type (`Collection<int>`) resolves.
        var type = ClrRef(e.GetProperty("type"));
        var name = e.GetProperty("method").GetString();
        // W1-S2 (#46) CONSUME-ONLY: bir2cir already RESOLVED the overload against the ref.dll MLC and stamped the
        // winning member's DECLARED param signature as `memberSig`; ilemit is a linker (exact structural match, hard
        // fail on 0/multi) — no arity probe, no name+arity first-pick, no assignability scoring, no silent downgrade.
        var mi = LinkClrMethod(type, name, e, instance);
        // A generic BCL method (`System.Array.Fill<T>(T[],T,int,int)`) resolved as its open DEFINITION must be
        // instantiated with the call's type args (threaded by bir2cir from the @ClrIntrinsic generic Kotlin callee),
        // or the emitted MethodSpec stays open -> "method/type not fully instantiated" at run. Non-generic targets
        // leave IsGenericMethodDefinition false, so this is a no-op there.
        if (mi.IsGenericMethodDefinition
            && e.TryGetProperty("typeArgs", out var clrTa) && clrTa.ValueKind == JsonValueKind.Array && clrTa.GetArrayLength() > 0)
            mi = ConstructedMethod(mi, clrTa.EnumerateArray().Select(a => MapType(a)).ToArray());
        // A value-type receiver's instance method needs a managed pointer (e.g. struct Vec2.Mag2()); the `constrained.`
        // dispatch (below) likewise needs the receiver ADDRESS. (A generic `Array<T>.clone()` is already retargeted by
        // bir2cir to the `System.Array` owner, so its `T[]` receiver is statically assignable — no cast needed here.)
        if (instance)
        {
            if (IsValueType(type)) EmitAddr(e.GetProperty("recv"));
            else EmitExpr(e.GetProperty("recv"));
        }
        EmitArgs(e.GetProperty("args"), ParametersOf(mi));
        // Dispatch is a bir2cir DECISION carried on the node (call | callvirt | constrained) — computed from the
        // resolved MethodInfo's IsVirtual/IsFinal + the owner value-type-ness + the `super` (issue #14 base-slot) flag.
        // ilemit no longer derives it from reflected `mi.IsVirtual/IsFinal`. A static call is unconditionally `call`.
        if (instance)
        {
            // `dispatch` is a REQUIRED bir2cir decision — a missing one is a producer defect, NOT a silent `callvirt`
            // default (which on a value-type owner is unverifiable CallVirtOnValueType). Fail loud (consume-only doctrine).
            if (!e.TryGetProperty("dispatch", out var dEl) || dEl.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"ilemit: clrInstance {type?.FullName}.{name} is missing its `dispatch` decision (bir2cir must carry call|callvirt|constrained — W1-S2 #46)");
            EmitClrDispatch(mi, dEl.GetString(), type);
        }
        else
            EmitMethod(_il, OpCodes.Call, mi);
        // A `ref T`-returning method used as a value -> dereference the managed pointer (value copy). The live-ref
        // form (`byrefOf(m())`, behind `var x by byref(m())`) passes deref:false to keep the pointer.
        var methodReturn = ReturnTypeOf(mi);
        if (methodReturn.IsByRef)
        {
            if (!deref) return methodReturn;
            var elem = methodReturn.GetElementType();
            _il.Emit(OpCodes.Ldobj, elem);
            return elem;
        }
        // TypeBuilder.GetMethod re-anchors the call onto the instantiation but leaves the method's RETURN type open
        // (e.g. `Task<Vec>::GetAwaiter()` reports `TaskAwaiter`1<!0>`, not `<Vec>`). The IL token is correct, but the
        // STATIC type we hand back must be the substituted one or the caller mis-types its temp/local — so trust the
        // BIR `ret` hint, which already carries the substituted type. (Only when the reflected return is still open.)
        if ((methodReturn.IsGenericParameter || methodReturn.ContainsGenericParameters)
            && e.TryGetProperty("ret", out var rh))
        {
            // Post type-flip the `ret` hint is a STRUCTURED TypeNode (`{"t":"tv","scope":"method",...}`); the legacy
            // form was a bare string. A method-scope Tv return (`IReadOnlyList<T>::get_Item` on the non-generic
            // `_CollectionsKt.firstOrNull<T>`) reflects as the OPEN interface param `!0` (type-scope, position 0) —
            // a standalone `!0` token in a non-generic method body is INVALID metadata (`box !0` -> BadImageFormat at
            // JIT). MapType resolves the structured Tv against `_curMethodParams` to the method's own `!!T`, so the
            // caller boxes/consumes the correct method-scope token. (String hint keeps the legacy ClrRef path.)
            Type hinted = rh.ValueKind == JsonValueKind.String ? TryResolveClr(rh.GetString())
                        : rh.ValueKind == JsonValueKind.Object ? TryMapType(rh) : null;
            if (hinted != null) return hinted;
        }
        return methodReturn;
    }

    Type[] NativeParameterTypes(JsonElement member) =>
        member.GetProperty("parameterTypes").EnumerateArray()
            .Select(t => TryResolveNativeType(t.GetString()))
            .ToArray();

    Type TryResolveNativeType(string spec)
    {
        try { return NativeType(spec); }
        catch { return null; }
    }

    // A type slot for an IL-opcode context (newarr elem / conv / default): a structured node resolves via MapType, a
    // bare type-IDENTITY string (a CLR-shorthand primitive, or a bare FQN) via the identity path below (the legacy
    // grammar prefixes are retired, #48).
    Type NativeType(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? MapType(DotKt.Bir.TypeNode.Read(e)) : NativeType(e.GetString());

    Type NativeType(string spec)
    {
        if (spec == null) return Bcl("System.Object");
        return spec switch
        {
            "void" or "int" or "long" or "double" or "float" or "bool" or "char" or "string" or
            "uint" or "ulong" or "byte" or "ushort" or "short" or "sbyte" or "object" => MapType(spec),
            _ => ClrRef(spec),
        };
    }

    static string NativeOwnerSpec(JsonElement node, JsonElement member) =>
        node.TryGetProperty("ownerType", out var ownerType) && ownerType.ValueKind != JsonValueKind.Null
            ? SlotName(ownerType)
            : SlotName(member.GetProperty("owner"));

    // ClrRef (generic-aware type resolution) that returns null instead of throwing.
    Type TryResolveClr(string spec) { try { return ClrRef(spec); } catch { return null; } }

    // MapType (structured-TypeNode resolution) that returns null instead of throwing.
    Type TryMapType(JsonElement e) { try { return MapType(e); } catch { return null; } }

    // ResolveType but returns null instead of throwing (for optional/best-effort overload resolution).
    Type TryResolveType(string name)
    {
        try { return ResolveType(name); } catch (NotSupportedException) { return null; }
    }

    // W1-S3 (#46 / #121) CONSUME-ONLY property GET. bir2cir (ClrMemberResolution) resolved the member against the ref.dll
    // and stamped a `member` discriminator: "accessor" (a real .NET property OR a DotKt `get_X` method — carries the
    // resolved accessor name + `memberSig` + `dispatch`) or "field" (a public/const field). ilemit no longer reclassifies
    // property vs get_ method vs field, and no longer derives dispatch from the reflected accessor.
    Type EmitClrPropGet(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        if (ClrMemberKind(e) == "accessor")
        {
            var getter = LinkClrMethod(type, e.GetProperty("accessor").GetString(), e, instance: !isStatic);
            if (!isStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
            if (isStatic) EmitMethod(_il, OpCodes.Call, getter);
            else EmitClrDispatch(getter, RequireDispatch(e, type, "clrPropGet"), type);   // call | callvirt | constrained (bir2cir decision)
            return ReturnTypeOf(getter);
        }
        // A .NET FIELD surfaced as a Kotlin property (bir2cir resolved member:"field"). A `const` (literal) field has no
        // storage — inline its value (a mechanical raw-constant fetch, not a KIND decision), exactly as C# does.
        var fld = ResolveClrPropField(type, e.GetProperty("name").GetString());
        if (fld.IsLiteral) return EmitLiteralValue(fld.GetRawConstantValue(), FieldTypeOf(fld));
        if (!isStatic && !fld.IsStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        MaybeVolatile(fld, e);
        _il.Emit(fld.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, fld);
        return FieldTypeOf(fld);
    }

    // W1-S3 (#46 / #121) CONSUME-ONLY property SET (twin of EmitClrPropGet).
    Type EmitClrPropSet(JsonElement e)
    {
        var type = ClrRef(e.GetProperty("type"));
        var isStatic = e.GetProperty("static").GetBoolean();
        if (ClrMemberKind(e) == "accessor")
        {
            var setter = LinkClrMethod(type, e.GetProperty("accessor").GetString(), e, instance: !isStatic);
            if (!isStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
            EmitArgs2(new[] { e.GetProperty("value") }, ParametersOf(setter));
            if (isStatic) EmitMethod(_il, OpCodes.Call, setter);
            else EmitClrDispatch(setter, RequireDispatch(e, type, "clrPropSet"), type);
            return Bcl("System.Void");
        }
        // A writable .NET FIELD surfaced as a Kotlin (mutable) property -> field store.
        var fld = ResolveClrPropField(type, e.GetProperty("name").GetString());
        if (!isStatic && !fld.IsStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        EmitNullableCoerced(e.GetProperty("value"), FieldTypeOf(fld));
        MaybeVolatile(fld, e);
        _il.Emit(fld.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld, fld);
        return Bcl("System.Void");
    }

    // The bir2cir `member` discriminator ("accessor" | "field"), a REQUIRED W1-S3 decision. A missing one is a producer
    // defect (ilemit must not re-derive the member kind), not a silent field fallback.
    static string ClrMemberKind(JsonElement e)
    {
        if (e.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.String) return m.GetString();
        throw new InvalidOperationException($"ilemit: clr property/field node is missing its `member` discriminator (bir2cir ClrMemberResolution must carry accessor|field — W1-S3 #46/#121)");
    }

    static string RequireDispatch(JsonElement e, Type type, string what)
    {
        if (e.TryGetProperty("dispatch", out var d) && d.ValueKind == JsonValueKind.String) return d.GetString();
        throw new InvalidOperationException($"ilemit: {what} on {type?.FullName} is missing its `dispatch` decision (bir2cir must carry call|callvirt|constrained — W1-S3 #46)");
    }

    // Resolve the FieldInfo for a `member:"field"` property node (bir2cir already decided the KIND is a field). GetField
    // walks base classes; a constructed generic over a TypeBuilder re-anchors on the open def. NOT a KIND probe — the
    // member is known to be a field, this only produces the handle.
    FieldInfo ResolveClrPropField(Type type, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;
        try { var fld = type.GetField(name, F); if (fld != null) return fld; }
        catch (NotSupportedException) { }
        if (type.IsConstructedGenericType)
        {
            var openFld = type.GetGenericTypeDefinition().GetField(name, F);
            if (openFld != null) return AnchorField(type, openFld);
        }
        throw new InvalidOperationException($"ilemit: field '{name}' resolved by bir2cir is absent on .NET type '{type}' (W1-S3 #46/#121)");
    }

    // `.NET event +=/-=` -> call the event's add/remove accessor with the handler bound as the event's OWN
    // delegate type (e.g. EventHandler), not the Func/Action the lambda would otherwise produce. The lifted
    // method's signature matches the delegate's Invoke (the reference KLIB typed the handler from the event's
    // handler signature), so `ldftn`+`newobj <EventDelegate>(object, IntPtr)` is verifiable — exactly what
    // `button.Click += (s,e)=>{}` lowers to in C#.
    // W1-S3 (#46 / #121 / #113) CONSUME-ONLY event add/remove. bir2cir (ClrMemberResolution) resolved the EventInfo off
    // the ref.dll and stamped the add/remove accessor NAME + `memberSig` (the [handlerDelegate] param) + `dispatch`.
    // ilemit LINKS the exact accessor (LinkClrMethod — hard-fails a missing/ambiguous slot, so the old unchecked
    // `GetEvent(...).GetAddMethod()` NRE on a missing/value-type/constructed-generic event is gone) and consumes the
    // carried dispatch. The handler delegate type flows from the resolved accessor's first param (== EventHandlerType).
    Type EmitClrEvent(JsonElement e, bool add)
    {
        var type = ClrRef(e.GetProperty("type"));
        bool isStatic = e.GetProperty("static").GetBoolean();
        var accessor = LinkClrMethod(type, e.GetProperty("accessor").GetString(), e, instance: !isStatic);
        var delType = ParametersOf(accessor)[0].ParameterType;   // == the event's EventHandlerType
        if (!isStatic) { if (IsValueType(type)) EmitAddr(e.GetProperty("recv")); else EmitExpr(e.GetProperty("recv")); }
        if (e.TryGetProperty("handlerExact", out var exact) && exact.GetBoolean())
            EmitExpr(e.GetProperty("handler"));
        else
            EmitHandlerAsDelegate(e.GetProperty("handler"), delType);
        if (isStatic) EmitMethod(_il, OpCodes.Call, accessor);
        else EmitClrDispatch(accessor, RequireDispatch(e, type, add ? "clrEventAdd" : "clrEventRemove"), type);
        return Bcl("System.Void");
    }

    // Resolve a newClosure node's ctor + invoke, INSTANTIATING the closure generic when it is a generic definition.
    // A capturing closure over an enclosing type param (`{ seed }` in `generateSequence<T>`) is a GENERIC class;
    // left as its open definition the `newobj Closure`1::.ctor(!0)` operand is OPEN -> a TypeLoadException at run.
    // Close it with the node's explicit `typeArgs`, else (C13a: kotc/bir2cir omitted them for the non-`this`-capturing
    // form) with the enclosing params matched by NAME (GenericParamByName). Shared by the
    // main newClosure emit and the delegate-arg binding path so neither can diverge.
    (ConstructorInfo Ctor, MethodInfo Invoke) ResolveClosure(JsonElement e)
    {
        var ct = _types[SlotName(e.GetProperty("closureType"))];
        ConstructorInfo ctor = ct.Ctor;
        MethodInfo invoke = ct.Methods[e.GetProperty("method").GetString()];
        Type constructed = null;
        if (e.TryGetProperty("typeArgs", out var taProp) && taProp.GetArrayLength() > 0)
            constructed = ConstructedType(ct.TB, taProp.EnumerateArray().Select(a => MapType(a)).ToArray());
        else if (ct.TB.IsGenericTypeDefinition)
            constructed = ConstructedType(ct.TB, ct.TB.GetGenericArguments().Select(gp => GenericParamByName(gp.Name)).ToArray());
        if (constructed != null)
        {
            ctor = AnchorConstructor(constructed, ct.Ctor);
            invoke = AnchorMethod(constructed, invoke);
        }
        return (ctor, invoke);
    }

    // §4.2/§4.3 (#187) — the body of a SYNTHESIZED field-like .NET event accessor. bir2cir (ClrEventImplBinding) resolved
    // the concrete delegate `D` off the ref.dll and inserted the `<E>$delegate : D` backing field; ilemit emits pure CLR
    // codegen from the `clrEventAccessorImpl{kind, name:<field>, delegateType:D}` directive — the C# field-like-event shape:
    //   add/remove -> a lock-free CAS loop (Delegate.Combine/Remove + Interlocked.CompareExchange<D>);
    //   raise      -> `field?.Invoke(args...)` (the null-conditional makes a zero-subscriber raise a safe no-op).
    // The method's trailing `ret` is appended by EmitTrailingRet (all three are void). ilemit knows NO Kotlin here — the
    // delegate + field are already resolved. (§9 — ClrEvent<T>/clrEvent() never reach this layer.)
    void EmitClrEventAccessorImpl(JsonElement e)
    {
        var kind = e.GetProperty("kind").GetString();
        var fieldName = e.GetProperty("name").GetString();
        var d = MapType(e.GetProperty("delegateType"));
        // S5 (#113): a missing backing field is a bir2cir/kotc synthesis defect — a legible breadcrumb, not an opaque miss.
        if (_curTi == null || !_curTi.Fields.TryGetValue(fieldName, out var field))
            throw new InvalidOperationException($"ilemit: clrEventAccessorImpl backing field '{fieldName}' is absent on '{_curTi?.TB?.Name}' (bir2cir ClrEventImplBinding must insert `<E>$delegate` — #187/#113)");
        if (kind == "raise") { EmitClrEventRaise(field, d); return; }
        EmitClrEventCas(field, d, add: kind == "add");
    }

    // The lock-free add/remove CAS loop over the backing delegate field (C#'s field-like-event accessor shape):
    //   D cur = this.field; do { D cmp = cur; D upd = (D)Delegate.Combine/Remove(cmp, value);
    //       cur = Interlocked.CompareExchange<D>(ref this.field, upd, cmp); } while (cur != cmp);
    // arg0 = this, arg1 = the handler `value`. Reference comparison (bne.un), not delegate equality.
    void EmitClrEventCas(FieldInfo field, Type d, bool add)
    {
        var combine = Bcl("System.Delegate").GetMethod(add ? "Combine" : "Remove", new[] { Bcl("System.Delegate"), Bcl("System.Delegate") });
        var cmpx = ConstructedMethod(
            Bcl("System.Threading.Interlocked").GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "CompareExchange" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1),
            d);
        var cur = _il.DeclareLocal(d); var cmp = _il.DeclareLocal(d); var upd = _il.DeclareLocal(d);
        var retry = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); EmitField(_il, OpCodes.Ldfld, field); _il.Emit(OpCodes.Stloc, cur);
        _il.MarkLabel(retry);
        _il.Emit(OpCodes.Ldloc, cur); _il.Emit(OpCodes.Stloc, cmp);
        _il.Emit(OpCodes.Ldloc, cmp); _il.Emit(OpCodes.Ldarg_1);
        EmitMethod(_il, OpCodes.Call, combine); _il.Emit(OpCodes.Castclass, d); _il.Emit(OpCodes.Stloc, upd);
        _il.Emit(OpCodes.Ldarg_0); EmitField(_il, OpCodes.Ldflda, field);
        _il.Emit(OpCodes.Ldloc, upd); _il.Emit(OpCodes.Ldloc, cmp);
        EmitMethod(_il, OpCodes.Call, cmpx); _il.Emit(OpCodes.Stloc, cur);
        _il.Emit(OpCodes.Ldloc, cur); _il.Emit(OpCodes.Ldloc, cmp); _il.Emit(OpCodes.Bne_Un, retry);
    }

    // `raise_<E>(args...)`: snapshot the backing field, and if non-null invoke it with the raise method's own params.
    // arg0 = this; the raise params (== D.Invoke's params) start at arg1. The null check = C#'s field-like `field?.Invoke`.
    void EmitClrEventRaise(FieldInfo field, Type d)
    {
        var invoke = d.GetMethod("Invoke");
        var handler = _il.DeclareLocal(d);
        var done = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_0); EmitField(_il, OpCodes.Ldfld, field); _il.Emit(OpCodes.Stloc, handler);
        _il.Emit(OpCodes.Ldloc, handler); _il.Emit(OpCodes.Brfalse, done);
        _il.Emit(OpCodes.Ldloc, handler);
        var n = ParametersOf(invoke).Length;
        for (int i = 0; i < n; i++) _il.Emit(OpCodes.Ldarg, checked((short)(i + 1)));
        EmitMethod(_il, OpCodes.Callvirt, invoke);
        _il.MarkLabel(done);
    }

    // Bind a lambda handler (newDelegate = non-capturing, newClosure = capturing) into a SPECIFIC delegate type.
    // Mirrors the newDelegate/newClosure cases but uses `want` (the event's delegate type) for the ctor.
    void EmitHandlerAsDelegate(JsonElement h, Type want)
    {
        // The delegate's Invoke return type: when it is a real (non-void) type while the lambda's NATURAL delegate is
        // void-returning (a Unit body maps to void), binding the void method-pointer into the ctor is not verifiable
        // -> self-build the natural void delegate and wrap it in a Unit-return adapter. InvokeRetOf (not want.GetMethod/
        // InvokeOf) so a TypeBuilder-arg `want` (`Func<Res,Unit>`) yields the CLOSED return type (`kotlin.Unit`) — a
        // by-name lookup throws and InvokeOf's ReturnType comes back unsubstituted.
        var invokeRet = InvokeRetOf(want);
        var k = h.GetProperty("k").GetString();
        if (invokeRet != Bcl("System.Void") && (k == "newDelegate" || k == "newClosure")
            && FuncRetType(h.GetProperty("funcType")) == Bcl("System.Void"))
        {
            var ft = EmitExpr(h);                             // the lambda's natural void delegate, on the stack
            EmitMethod(_il, OpCodes.Ldftn, UnitWrapAdapter(ft, invokeRet, FuncArgTypes(h.GetProperty("funcType")).ToArray()));
            EmitDelegateCtor(_il, want);
            return;
        }
        switch (k)
        {
            case "newDelegate":
                // Mirrors Emitter.Expressions: mandatory calleeOwner selects the one file class and sig selects the
                // overload. Missing/misspelled ownership is malformed CIR, never a global name lookup (#204).
                var dname = h.GetProperty("method").GetString();
                var dsig = SigNodes(h);
                var dtarget = FindCalleeOwnedStatic(h, "event newDelegate", dname, dsig, CalledMethodArity(h));
                _il.Emit(OpCodes.Ldnull);
                EmitMethod(_il, OpCodes.Ldftn, dtarget);
                EmitDelegateCtor(_il, want);
                break;
            case "newClosure":
                var (cctor, cinvoke) = ResolveClosure(h);
                foreach (var c in h.GetProperty("captures").EnumerateArray()) EmitExpr(c);
                EmitConstructor(_il, OpCodes.Newobj, cctor);
                EmitMethod(_il, OpCodes.Ldftn, cinvoke);
                EmitDelegateCtor(_il, want);
                break;
            default:
                // A stored handler value (a Func/Action local/field). Re-wrap it into the event's delegate
                // type via its Invoke — `new EventDelegate(value.Invoke)`. Two wrappers around the SAME stored
                // value share target+method, so Delegate equality holds and `-=` removes the right handler.
                var src = EmitExpr(h);                       // stack: the stored delegate value
                _il.Emit(OpCodes.Dup);
                EmitMethod(_il, OpCodes.Ldvirtftn, src.GetMethod("Invoke"));
                EmitDelegateCtor(_il, want);
                break;
        }
    }

}
