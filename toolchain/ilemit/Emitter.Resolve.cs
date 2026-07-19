// AUTO-SPLIT from Program.cs — part of the `Emitter` partial class (see Program.cs for the overview).
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Member resolution: fields/methods/ctors against TypeBuilders + reflected BCL, sig-key matching, type lookup.
sealed partial class Emitter
{
    // Resolve a field for emit; out-param gives the substituted (concrete) field type for boxing decisions.
    FieldInfo ResolveField(string spec, string name, out Type fieldType)
        => ResolveField(ParseOwner(spec), name, out fieldType);

    // Structured owner overload (keeps the constructed-generic instantiation — see ResolveMethod's overload note).
    FieldInfo ResolveField((string open, Type constructed) owner, string name, out Type fieldType)
    {
        var (open, constructed) = owner;
        // A REFERENCED generic owner constructed from PURE reflection types (NOT a TypeBuilder instantiation): reflect
        // the field directly on the constructed instantiation — its GetField carries the substituted field type, so no
        // TypeBuilder.GetField re-anchoring is needed. Mirrors ResolveMethod's external-constructed branch. (Reaches a
        // referenced data class's public fields, e.g. `kotlin.Pair[..]`.first/.second from `partition`/`associate`.)
        if (constructed != null && !_types.ContainsKey(open) && !IsTbInstantiation(constructed))
        {
            var rf = FindReflectedField(constructed, name) ?? throw new NotSupportedException($"field {open}.{name} not found");
            fieldType = rf.FieldType;
            return rf;
        }
        var fb = FindField(open, name);
        if (constructed == null)
        {
            fieldType = fb.FieldType;
            // Mirror of ResolveMethod's #84-I generic-base fix, FIELD side: a NON-generic subclass
            // (`class IntBox : Base<Int>`) reading a FIELD INHERITED from a GENERIC base. FindField walked the base
            // chain and returned the OPEN `Base`1::slot` FieldBuilder, whose bare operand is "not fully instantiated"
            // at the access site (ilverify: get_GenericParameters IndexOutOfRange). Anchor it onto the owner's
            // CONSTRUCTED base instantiation (`Base<Int>`, set as the owner TypeBuilder's parent) via
            // AnchorInheritedFieldOnBase — the same re-anchoring the constructed-owner path does below.
            if (fb.DeclaringType is { IsGenericTypeDefinition: true }
                && _types.TryGetValue(open, out var oti) && oti.TB is { } ownerTb
                && !ReferenceEquals(fb.DeclaringType, ownerTb)
                && AnchorInheritedFieldOnBase(ownerTb, fb, out var bft) is { } anchoredF)
            { fieldType = bft; return anchoredF; }
            return fb;
        }
        fieldType = Subst(fb.FieldType, constructed.GetGenericArguments());
        // A field DECLARED on `constructed`'s own generic def anchors directly (`Cell<!T>`'s own `item`, or an
        // external `Sub<String>`'s own field). An INHERITED field — `fb` declared on a generic BASE, not on
        // `constructed`'s def — makes TypeBuilder.GetField throw "field must be declared on the generic type
        // definition" (the #91 fault: `Wrap<T> : Base<T>` reading `this.slot` self-instantiated, or `Sub<String>.slot`
        // via a constructed receiver). Anchor it onto the constructed base instantiation (`Base<!T>` / `Base<String>`),
        // mirroring ResolveMethod's inherited-method path. Self-references reach here as `def.MakeGenericType(def's own
        // GenericTypeParameterBuilders)` (never the bare def), so the own-field anchoring and base-walk are both valid.
        try { return TypeBuilder.GetField(constructed, fb); }
        catch (ArgumentException) { return AnchorInheritedFieldOnBase(constructed, fb, out fieldType) ?? fb; }
    }

    // `fb` is INHERITED — declared on a generic BASE class, not on `constructed`'s own generic def. A field access must
    // reference it on the constructed base instantiation (`class Sub<T> : Base<T>` -> `Base<!T>::slot`, or `Sub<String>`
    // -> `Base<String>::slot`), NOT the open `Base`1` (a bare `Base`1::slot` operand is "not fully instantiated" -> the
    // JIT raises InvalidProgram, and ilverify crashes in get_GenericParameters). Walk the constructed receiver's
    // base-CLASS chain for the instantiation whose generic def is fb's declaring type, then anchor via
    // TypeBuilder.GetField. Mirrors AnchorInheritedOnBase (method side). Returns null when no such base instantiation
    // exists — the caller keeps the open FieldBuilder.
    FieldInfo AnchorInheritedFieldOnBase(Type constructed, FieldInfo fb, out Type fieldType)
    {
        fieldType = fb.FieldType;
        var targetDef = fb.DeclaringType;
        for (var bt = constructed.BaseType; bt != null; bt = bt.BaseType)
            if (bt.IsGenericType && !bt.IsGenericTypeDefinition && ReferenceEquals(bt.GetGenericTypeDefinition(), targetDef))
            {
                try { var anchored = TypeBuilder.GetField(bt, fb); fieldType = Subst(fb.FieldType, bt.GetGenericArguments()); return anchored; }
                catch (ArgumentException) { return null; }
            }
        return null;
    }

    // Resolve a field by name on an already-RESOLVED (referenced .NET / baked) type, walking its base-class chain
    // (reflection's GetField already includes inherited members). Pure CLR resolution; null if absent.
    static FieldInfo FindReflectedField(Type t, string name) =>
        t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

    // Resolve a method for emit; out-param gives the substituted (concrete) return type for boxing decisions.
    MethodInfo ResolveMethod(string spec, string name, out Type retType, string sig = null)
        => ResolveMethod(ParseOwner(spec), name, out retType, sig);

    // Structured owner overload: a constructed-generic owner slot (`kotlin.Pair[int,int]`) arriving as a native
    // TypeNode.Fqn must keep its ARGS — `SlotName` collapses the Fqn to its open name, which would resolve the member
    // on the OPEN generic def (`kotlin.Pair`2::get_first`), an invalid cross-assembly memberref -> runtime
    // TypeLoadException. ParseOwnerSlot preserves the instantiation; this overload consumes the pre-parsed owner.
    MethodInfo ResolveMethod((string open, Type constructed) owner, string name, out Type retType, string sig = null)
    {
        var (open, constructed) = owner;
        // A REFERENCED generic owner constructed from PURE reflection types (NOT a TypeBuilder instantiation): resolve
        // the member directly on the constructed instantiation — its GetMethods carry the substituted signature, so no
        // TypeBuilder.GetMethod re-anchoring (below) is needed. A referenced-generic instantiated with an EMITTED
        // (TypeBuilder) arg stays on the TypeBuilder.GetMethod path below (reflection GetMethods throws on such a type).
        if (constructed != null && !_types.ContainsKey(open) && !IsTbInstantiation(constructed))
        {
            var argc = sig == null ? -1 : (sig.Length == 0 ? 0 : SplitTopLevel(sig).Count);
            // Prefer a SIG-DRIVEN pick: a referenced constructed-generic owner can carry same-name/same-arity overloads
            // that differ only in a PARAM's generic-type owner (SequenceScope<T>.yieldAll$dotkt_suspend over
            // Iterator<T> vs IEnumerable<T> vs Sequence<T>) — arity alone binds an arbitrary one -> BadImageFormat.
            // FindReflectedMethodBySig maps the declared-callee `sig` tokens (structurally for open `gp:T` args) to
            // disambiguate; fall back to the arity pick when no sig is carried (or it can't uniquely resolve).
            // A miss must be a LEGIBLE error (and lets callInstance's dynRet fallback catch it) — an unchecked
            // deref here was an opaque NRE.
            var rrm = FindReflectedMethodBySig(constructed, name, sig)
                ?? FindReflectedMethod(constructed, name, argc)
                ?? throw new NotSupportedException($"method {name} not found on referenced type {constructed}");
            retType = rrm.ReturnType;
            return rrm;
        }
        var mb = FindMethod(open, name, sig)
            ?? throw new NotSupportedException($"method {open}.{name}({sig}) not found (external owner did not resolve or lacks the member)");
        if (constructed == null)
        {
            retType = mb.ReturnType;
            // A NON-generic subclass (`class Sub : Base<Int>`, `class C : Seg<C>`) calling a method INHERITED from a
            // GENERIC base: FindMethod walked the base chain and returned the OPEN `Base`1::m` MethodBuilder, whose bare
            // operand is "not fully instantiated" at the call site (the #84 I generic-base fault). Anchor it onto the
            // owner's CONSTRUCTED base instantiation (`Base<Int>` / `Seg<C>`, set as the owner TypeBuilder's parent),
            // walking the base chain via AnchorInheritedOnBase — the same re-anchoring the constructed-owner path does.
            if (mb.DeclaringType is { IsGenericTypeDefinition: true }
                && _types.TryGetValue(open, out var oti) && oti.TB is { } ownerTb
                && !ReferenceEquals(mb.DeclaringType, ownerTb)
                && AnchorInheritedOnBase(ownerTb, mb, out var brt) is { } anchored)
            { retType = brt; return anchored; }
            return mb;
        }
        // The owner constructed with its OWN class type parameters (`RingBuffer<T>` referenced from inside
        // RingBuffer<T>) is the self instantiation. A call must reference the method on that self-instantiation
        // (`C<!0>::m`), NOT the open type def (`C`1::m`) — the open form is "not fully instantiated" at runtime (any
        // self method-call `this.b()` inside a generic class). EXCEPTION: a generic-METHOD self-call must keep the open
        // MethodBuilder, because TypeBuilder.GetMethod yields a MethodBuilderInstantiation that can't be
        // MakeGenericMethod'd (`this.toArray<object>()`); ApplyTypeArgs instantiates the open mb instead.
        if (IsSelfInstantiation(constructed))
        {
            retType = mb.ReturnType;
            if (mb.IsGenericMethodDefinition) return mb;
            // TypeBuilder.GetMethod requires `mb` declared on `constructed`'s OWN generic def. An INHERITED self-call
            // (mb on a generic base, `class D<T> : Base<T>` — `this.show()`) throws; anchor it onto the CONSTRUCTED
            // base instantiation (`Base<!T>`), not the open def (`Base``1::m` is "not fully instantiated").
            try { return TypeBuilder.GetMethod(constructed, mb); }
            catch (ArgumentException) { return AnchorInheritedOnBase(constructed, mb, out retType) ?? mb; }
        }
        retType = Subst(mb.ReturnType, constructed.GetGenericArguments());
        // An INHERITED method on a NON-self constructed generic (mb declared on a base/interface, not on `constructed`'s
        // own generic def — `D<int> : Base<int>`.get_x, or AbstractMutableCollection<E>'s inherited iterator()) throws the
        // same "method must be declared on the generic type definition" as the self case -> anchor onto the constructed
        // base instantiation (`Base<int>`); only fall back to the open MethodBuilder when no such base exists (interface).
        try { return TypeBuilder.GetMethod(constructed, mb); }
        catch (ArgumentException) { return AnchorInheritedOnBase(constructed, mb, out retType) ?? mb; }
    }

    // `mb` is INHERITED — declared on a generic BASE class, not on `constructed`'s own generic def. A call must
    // reference it on the constructed base instantiation (`class D<T> : Base<T>()` -> `Base<!T>::m`, or `D<int>` ->
    // `Base<int>::m`), NOT the open `Base<>` (a bare `Base``1::m` operand is "not fully instantiated" -> the JIT
    // raises InvalidProgram / "not fully instantiated"). Walk the constructed receiver's base-CLASS chain for the
    // instantiation whose generic def is mb's declaring type, then anchor via TypeBuilder.GetMethod. Returns null
    // when the declaring type is an INTERFACE (not on the class chain) — the caller keeps the open MethodBuilder.
    MethodInfo AnchorInheritedOnBase(Type constructed, MethodInfo mb, out Type retType)
    {
        retType = mb.ReturnType;
        var targetDef = mb.DeclaringType;
        for (var bt = constructed.BaseType; bt != null; bt = bt.BaseType)
            if (bt.IsGenericType && !bt.IsGenericTypeDefinition && ReferenceEquals(bt.GetGenericTypeDefinition(), targetDef))
            {
                try { var anchored = TypeBuilder.GetMethod(bt, mb); retType = Subst(mb.ReturnType, bt.GetGenericArguments()); return anchored; }
                catch (ArgumentException) { return null; }
            }
        return null;
    }

    // A property read/write on an EXTERNAL type must go through the public accessor (`get_`/`set_<name>`), NOT the
    // backing field: the CLR property model gives every Kotlin property a PRIVATE backing field, which is inaccessible
    // cross-assembly (a direct Ldfld/Stfld -> FieldAccessException at runtime, e.g. `kotlin.Pair`.first/.second read
    // via a destructuring `component1()`/`component2()` that kotc lowers to a field access). Returns the accessor
    // anchored on the (possibly constructed-generic) owner, or null when the owner is emitted in THIS assembly (its
    // backing field is directly accessible) or no such accessor exists (a public `@ClrField` -> keep the direct field).
    MethodInfo ExternalPropAccessor(string spec, string accessor)
        => ExternalPropAccessor(ParseOwner(spec), accessor);

    // Structured owner overload (keeps the constructed-generic instantiation — see ResolveMethod's overload note).
    MethodInfo ExternalPropAccessor((string open, Type constructed) owner, string accessor)
    {
        var (open, constructed) = owner;
        if (_types.ContainsKey(open)) return null;
        // Pure-reflection constructed generic: resolve directly on the instantiation (its accessor carries the
        // SUBSTITUTED return/param types, so no TypeBuilder re-anchoring is needed) — mirrors ResolveMethod's branch.
        if (constructed != null && !IsTbInstantiation(constructed))
            return FindReflectedMethod(constructed, accessor);
        var mb = FindMethod(open, accessor);
        if (mb == null) return null;
        if (constructed == null) return mb;
        try { return TypeBuilder.GetMethod(constructed, mb); }
        catch (ArgumentException) { return mb; }
    }

    // True when `constructed` is a generic type instantiated with exactly its own open definition's type parameters
    // (in order) — i.e. `C<T0,T1,…>` referenced from within C, which is the same as the open type in emitted IL.
    static bool IsSelfInstantiation(Type constructed)
    {
        if (!constructed.IsGenericType || constructed.IsGenericTypeDefinition) return false;
        if (constructed.GetGenericTypeDefinition() is not TypeBuilder def) return false;
        var args = constructed.GetGenericArguments();
        var pars = def.GetGenericArguments();
        if (args.Length != pars.Length) return false;
        for (int i = 0; i < args.Length; i++) if (!ReferenceEquals(args[i], pars[i])) return false;
        return true;
    }

    // A BCL constructed generic (List<T>, HashSet<T>, Dictionary<K,V>) whose type argument is an EMITTED type
    // (a TypeBuilderInstantiation) refuses reflection — `GetConstructor`/`GetMethod` throw "does not support
    // resolving members" (feedback item 12). Re-anchor the OPEN definition's member onto the constructed type via
    // the static TypeBuilder.GetX helpers, exactly like ResolveField/ResolveMethod do for emitted generics.
    static bool IsTbInstantiation(Type t) =>
        t.IsGenericType && !t.IsGenericTypeDefinition &&
        // The type's own open definition is a TypeBuilder (`Iterator<int>` while Iterator is being emitted), OR one of
        // its args transitively involves a TypeBuilder. The first clause is what `ContainsTypeBuilder` also needed: a
        // constructed-generic-of-a-TypeBuilder is itself a TypeBuilderInstantiation but is not `is TypeBuilder`.
        (t.GetGenericTypeDefinition() is TypeBuilder
         || t.GetGenericArguments().Any(a => a is TypeBuilder || a is GenericTypeParameterBuilder || (a.IsGenericType && IsTbInstantiation(a))));

    // True when `t` is (or transitively contains) a generic PARAMETER — a GenericTypeParameterBuilder of the enclosing
    // emitting context. Distinguishes a concrete owner instantiation (`Holder<int>`) from an erased-context one
    // (`Holder<E>` referenced from inside another generic). Recursive, NOT Type.ContainsGenericParameters (unreliable
    // on un-baked builder instantiations); GenericTypeParameterBuilder reports IsGenericParameter reliably.
    static bool ContainsGenericParam(Type t) =>
        t.IsGenericParameter || (t.IsGenericType && t.GetGenericArguments().Any(ContainsGenericParam));

    static ConstructorInfo GenericCtor(Type constructed, params Type[] argTypes) =>
        IsTbInstantiation(constructed)
            ? TypeBuilder.GetConstructor(constructed, constructed.GetGenericTypeDefinition().GetConstructor(argTypes))
            : constructed.GetConstructor(argTypes);

    static MethodInfo GenericMethod(Type constructed, string name) =>
        IsTbInstantiation(constructed)
            ? TypeBuilder.GetMethod(constructed, constructed.GetGenericTypeDefinition().GetMethod(name))
            : constructed.GetMethod(name);

    // Substitute an open interface's own generic parameters (positionally = `typeArgs`) throughout a base-interface
    // reference as declared on that open def — including CONSTRUCTED args (`ICollection<KeyValuePair<K,V>>` with
    // K:=string,V:=int -> `ICollection<KeyValuePair<string,int>>`). Every generic parameter appearing in such a
    // reference is declared by the open type, so GenericParameterPosition indexes `typeArgs` directly.
    static Type SubstituteIfaceArgs(Type t, Type[] typeArgs)
    {
        if (t.IsGenericParameter) return typeArgs[t.GenericParameterPosition];
        if (!t.IsGenericType) return t;
        var args = t.GetGenericArguments().Select(a => SubstituteIfaceArgs(a, typeArgs)).ToArray();
        return t.GetGenericTypeDefinition().MakeGenericType(args);
    }

    // Recursively replace the generic-parameter references in `from` (a method's OPEN type params) with the concrete
    // `to` (its call-site type args), reaching NESTED positions (a `Func<T,R>` param -> `Func<Res,int>`). Matches by
    // reference identity (reliable for a reflected method's own params) AND by method-scoped position (a
    // MethodBuilderInstantiation may hand back normalized param objects that are not reference-equal). Element/generic
    // structure is rebuilt; a non-generic leaf and a TYPE-scoped param are returned unchanged.
    static Type SubstituteMethodArgs(Type t, Type[] from, Type[] to)
    {
        for (int i = 0; i < from.Length && i < to.Length; i++) if (ReferenceEquals(t, from[i])) return to[i];
        if (t.IsGenericParameter)
            return t.DeclaringMethod != null && t.GenericParameterPosition < to.Length ? to[t.GenericParameterPosition] : t;
        if (t.HasElementType)
        {
            var e = SubstituteMethodArgs(t.GetElementType(), from, to);
            if (ReferenceEquals(e, t.GetElementType())) return t;
            return t.IsArray ? (t.GetArrayRank() == 1 ? e.MakeArrayType() : e.MakeArrayType(t.GetArrayRank()))
                : t.IsByRef ? e.MakeByRefType() : t.IsPointer ? e.MakePointerType() : t;
        }
        var ga = t.GetGenericArguments();
        if (ga.Length == 0) return t;
        var subd = ga.Select(a => SubstituteMethodArgs(a, from, to)).ToArray();
        return subd.SequenceEqual(ga) ? t : t.GetGenericTypeDefinition().MakeGenericType(subd);
    }

    // Overload disambiguation for interface-slot wiring: does a body method's declared param type satisfy the
    // interface method's (substituted) param type? Reference/Type equality first; builders vs runtime
    // instantiations of the same shape compare by name (TypeBuilderInstantiation instances are not reference-equal
    // even for identical shapes). Deliberately shallow — the caller only disambiguates same-name OVERLOADS, whose
    // param lists differ at the top level (CompareTo(Ver) vs CompareTo(object)).
    static bool SlotParamMatches(Type body, Type iface) =>
        ReferenceEquals(body, iface) || body == iface
        || (body.Name == iface.Name && (body.Namespace ?? "") == (iface.Namespace ?? ""));

    // A STATIC method declared on a GENERIC emitted class (a Kotlin companion fun of a generic class —
    // `Result<T>`'s `fun <T> success(value: T)` emitted as a static generic method on `Result`1`) resolved via a
    // bare owner spec is an open MethodBuilder. Emitting a call with that open-typedef parent from ANOTHER class
    // is invalid IL (`call kotlin.Result`1::success<T>` -> InvalidProgramException at JIT: a member of a generic
    // type must be referenced through a constructed typespec). Anchor it onto a concrete instantiation — `object`
    // for each class param: a Kotlin companion member cannot reference the enclosing class's type parameters, so
    // every instantiation is signature-identical and `object` is canonical (Codex-confirmed: the documented
    // TypeBuilder.GetMethod owner-form; ApplyTypeArgs' concrete-owner branch then MakeGenericMethod's the anchored
    // method with the call's own type args). No-op for non-generic owners and non-builder methods.
    MethodInfo AnchorOpenGenericOwnerStatic(MethodInfo m)
    {
        if (m == null || !m.IsStatic) return m;
        // LOCAL emitted generic owner: anchor the open MethodBuilder onto the `object`-instantiation.
        if (m is MethodBuilder mb)
        {
            if (mb.DeclaringType is not TypeBuilder tb || !tb.IsGenericTypeDefinition) return m;
            var constructed = tb.MakeGenericType(tb.GetGenericArguments().Select(_ => (Type)typeof(object)).ToArray());
            var anchored = TypeBuilder.GetMethod(constructed, mb);
            // Keep the param-type record visible through the anchored identity (call-site boxing decisions).
            if (_mparams.TryGetValue(mb, out var ps)) _mparams[anchored] = ps;
            return anchored;
        }
        // EXTERNAL (referenced .NET / rt-stdlib) reflection static on a generic type DEFINITION — the SAME problem for
        // any cross-assembly call to a static on a generic type (`kotlin.Result`1::success`/`failure`, …): FindMethod
        // resolves the member on the open `C`1` typedef, and emitting a call scoped to that open typedef is an invalid
        // memberref (runtime `TypeLoadException: Could not load type 'C`1' from assembly '<app>'`). Anchor onto the
        // `object`-instantiation exactly like the local path — a Kotlin companion static cannot reference the enclosing
        // class's type params, so every instantiation is signature-identical and `object` is canonical (this mirrors
        // the stdlib's OWN emitted IL: `call C`1<object>::success<…>(…)`). Match the constructed owner's member by
        // (module, metadata token): a method on a constructed RuntimeType instantiation shares its definition's token.
        // ApplyTypeArgs then MakeGenericMethod's the anchored method with the call's own type args.
        if (m.DeclaringType is not { IsGenericTypeDefinition: true } odt) return m;
        var con = odt.MakeGenericType(odt.GetGenericArguments().Select(_ => (Type)typeof(object)).ToArray());
        return con.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                  .Single(x => x.Module == m.Module && x.MetadataToken == m.MetadataToken);
    }

    MethodInfo ApplyTypeArgs(MethodInfo m, JsonElement e, out Type retType, out Type[] paramTypes)
    {
        // Defense: an unresolved call (FindMethod/FindStatic returned null — e.g. a bad owner the CIR should never carry)
        // must fail with a legible message naming the call, not a cryptic Dictionary ArgumentNullException(key) below.
        if (m == null)
        {
            var mn = e.TryGetProperty("method", out var mnEl) && mnEl.ValueKind == JsonValueKind.String ? mnEl.GetString() : "?";
            var on = e.TryGetProperty("owner", out var onEl) && onEl.ValueKind != JsonValueKind.Null ? SlotName(onEl) : null;
            throw new NotSupportedException($"unresolved method: {(on != null ? on + "." : "")}{mn}");
        }
        var ps = _mparams.TryGetValue(m, out var p) ? p : null;
        if (e.TryGetProperty("typeArgs", out var ta) && ta.GetArrayLength() > 0)
        {
            var targs = ta.EnumerateArray().Select(x => MapType(x)).ToArray();
            // Substitute by REFERENCE IDENTITY (the method's own gp builders -> the concrete type args), NOT by
            // reflecting `DeclaringMethod`/`GenericParameterPosition` — those are null/garbage on an un-baked
            // MethodBuilder, which silently dropped the substitution and boxed value args. Identity is reliable.
            var sub = new Dictionary<Type, Type>();
            if (m is MethodBuilder mbk && _methodTypeParams.TryGetValue(mbk, out var gps))
            {
                int k = 0;
                foreach (var gp in gps.Values) { if (k < targs.Length) sub[gp] = targs[k]; k++; }
            }
            // A generic method on a CONSTRUCTED-generic TypeBuilder owner (non-self, e.g. `ringBuffer<E>.toArray<T>()`)
            // is a MethodBuilderInstantiation whose MakeGenericMethod is unsupported. Instantiate the OPEN method's
            // generic params. Reflection.Emit has NO API for a generic method on a constructed-generic TypeBuilder
            // owner (TypeBuilder.GetMethod wants a generic-method-DEFINITION; MethodBuilderInstantiation can't
            // MakeGenericMethod). The owner here is constructed with enclosing type params (e.g. ringBuffer<E>.toArray
            // <T>() from inside another generic), which erases to the open owner in IL, so use the OPEN method's
            // instantiation directly — the same shape the self-instantiation path emits.
            if (m is not MethodBuilder && m.DeclaringType is { IsGenericType: true } dt && !dt.IsGenericTypeDefinition
                && dt.GetGenericTypeDefinition() is TypeBuilder openTb)
            {
                var nm = e.GetProperty("method").GetString();
                // Detect a generic MethodBuilder via _methodTypeParams (IsGenericMethodDefinition/GetGenericArguments
                // are unreliable on an un-baked MethodBuilder).
                var openMb = _types.Values.FirstOrDefault(t => ReferenceEquals(t.TB, openTb))?.Methods.Values
                    .OfType<MethodBuilder>().FirstOrDefault(b => b.Name == nm
                        && _methodTypeParams.TryGetValue(b, out var g) && g.Count == targs.Length);
                if (openMb != null && _methodTypeParams.TryGetValue(openMb, out var ogps))
                {
                    int k = 0;
                    foreach (var gp in ogps.Values) { if (k < targs.Length) sub[gp] = targs[k]; k++; }
                    // A CONCRETE owner instantiation (`Holder<int>.pairWith<string>` — a call site OUTSIDE any generic
                    // context, e.g. main): the open-method form below loses the container's `<int>` and throws "not
                    // fully instantiated" at runtime. Here the anchored MethodOnTypeBuilderInstantiation (m, produced
                    // by ResolveMethod's TypeBuilder.GetMethod) DOES support MakeGenericMethod (the documented
                    // TypeBuilder.GetMethod-then-MakeGenericMethod order; verified on .NET 10 persisted emit), carrying
                    // BOTH instantiations. Gated STRICTLY to owners with no generic-parameter args so the erased-context
                    // path below (the rt-stdlib self/enclosing-generic case) is untouched. KNOWN EDGE (Codex review,
                    // no failing sample): a MIXED owner (`Holder<int, U>` — concrete + enclosing-generic args) still
                    // takes the open path and would lose the concrete arg; if such CIR ever appears, route it here too.
                    if (!dt.GetGenericArguments().Any(ContainsGenericParam))
                    {
                        var cpars = openTb.GetGenericArguments();
                        var cargs = dt.GetGenericArguments();
                        for (int i = 0; i < cpars.Length && i < cargs.Length; i++) sub[cpars[i]] = cargs[i];
                        retType = sub.TryGetValue(openMb.ReturnType, out var cr) ? cr : m.ReturnType;
                        paramTypes = _mparams.TryGetValue(openMb, out var ops)
                            ? ops.Select(x => sub.TryGetValue(x, out var s) ? s : x).ToArray() : ps;
                        return m.MakeGenericMethod(targs);
                    }
                    // Owner constructed with enclosing GENERIC PARAMS (`ringBuffer<E>.toArray<T>()` from inside another
                    // generic) — erases to the open owner in IL, so the OPEN method's instantiation is the right shape.
                    retType = sub.TryGetValue(openMb.ReturnType, out var or) ? or : m.ReturnType;
                    paramTypes = ps;
                    return openMb.MakeGenericMethod(targs);
                }
            }
            retType = sub.TryGetValue(m.ReturnType, out var r) ? r : m.ReturnType;
            paramTypes = ps?.Select(x => sub.TryGetValue(x, out var s) ? s : x).ToArray();
            // A specialized NON-generic overload can still carry the generic call's typeArgs: Kotlin specializes
            // `maxOrNull`/`sum`/`min` for Double/Float as a non-generic `Iterable<Double>.maxOrNull(): Double?`, but the
            // call site keeps `typeArgs=[Double]` from the generic `<T>` form. MakeGenericMethod throws on a non-generic
            // method ("not a GenericMethodDefinition"). When the resolved REFERENCED method is not a generic definition,
            // FindMethod already picked the right specialization — use it as-is. (A MethodBuilder reports
            // IsGenericMethodDefinition unreliably pre-bake, so this guards only reflected referenced methods.)
            if ((m is not MethodBuilder && !m.IsGenericMethodDefinition)
                || m.GetGenericArguments().Length != targs.Length) { retType = m.ReturnType; paramTypes = ps; return m; }
            var inst = m.MakeGenericMethod(targs);
            // A pure-reflection generic method (an EXTERNAL rt-stdlib static, e.g. `Result`1<object>::success<T>`
            // anchored by AnchorOpenGenericOwnerStatic) carries no `_mparams`/`_methodTypeParams` record, so `sub` is
            // empty and `ps` is null — read the concrete signature straight off the instantiation instead, so the
            // return type and value-arg boxing decisions are correct. Gated to reflection instantiations whose owner is
            // NOT a TypeBuilder instantiation (those go through the branches above / can't be reflected pre-bake).
            // ps==null => a REFERENCED method (an emitted MethodBuilder records its params in _mparams). Read the
            // concrete signature straight off the instantiation so paramTypes isn't left NULL — a null paramTypes makes
            // EmitArgsTyped emit each arg RAW (no target), so a lambda arg to a stdlib method whose param is the
            // synthetic `KFunc` delegate (a Kotlin function type over a stdlib TypeBuilder, e.g. MapsKt.mapValues's
            // `(Map.Entry)->R`) is built as `System.Func` and never rewrapped -> ilverify StackUnexpected [found
            // System.Func][expected KFunc]. Covers a generic static on a NON-generic file class (MapsKt) too — the
            // prior `DeclaringType.IsGenericType` guard only caught external-generic owners (Result`1). Excludes a
            // MethodBuilderInstantiation owner (GetParameters throws pre-bake), which never reaches here (ps!=null).
            if (ps == null && inst is not MethodBuilder
                && (inst.DeclaringType is not { IsGenericType: true } idt || !IsTbInstantiation(idt)))
            {
                // When `inst` is a MethodBuilderInstantiation (an external generic method instantiated over a
                // TypeBuilder arg — `AutoCloseableKt.use<Res,R>`, Res a user class being emitted), its GetParameters()/
                // ReturnType come back with the method's type params UNSUBSTITUTED (open `Func<T,R>`). Substitute them
                // to the concrete `targs` so a nested delegate param (`Func<T,R>` -> `Func<Res,int>`) is a rewrap
                // target — else the block arg is emitted against the open param and self-built as a mismatched KFunc
                // (ilverify StackUnexpected). A runtime instantiation already yields concrete members -> no-op.
                // Substitute over the OPEN reflected method `m` — its param generic-params ARE reference-equal to
                // `m.GetGenericArguments()`, so SubstituteMethodArgs matches them reliably. `inst.GetParameters()` may
                // hand back normalized param objects with a null DeclaringMethod, defeating the positional fallback.
                var openGps = m.GetGenericArguments();
                retType = SubstituteMethodArgs(m.ReturnType, openGps, targs);
                paramTypes = m.GetParameters().Select(p => SubstituteMethodArgs(p.ParameterType, openGps, targs)).ToArray();
            }
            return inst;
        }
        retType = m.ReturnType;
        paramTypes = ps;
        return m;
    }

    // Members may be declared on a base type (inherited / fake-overridden); walk the chain.
    FieldInfo FindField(string typeName, string name)
    {
        // A type NOT in this assembly's `_types` is EXTERNAL (a referenced .NET / rt-stdlib type) -> reflect the field
        // on the resolved type instead of indexing `_types` (which would KeyNotFound). Mirrors FindMethod's external
        // branch; reaches a referenced type's public/static fields (e.g. a data class field on the rt stdlib dll).
        if (!_types.ContainsKey(typeName))
        {
            Type ext = null;
            try { ext = ClrRef(typeName); } catch (NotSupportedException) { }
            if (ext == null && !typeName.Contains('`'))
                for (int arity = 1; arity <= 8; arity++)
                    if (TryResolveType(typeName + "`" + arity) is { } cand)
                    {
                        if (ext != null) { ext = null; break; }
                        ext = cand;
                    }
            return ext == null ? null : FindReflectedField(ext, name);
        }
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(BareTypeKey(ti.BaseName)) ? _types[BareTypeKey(ti.BaseName)] : null)
            if (ti.Fields.TryGetValue(name, out var f)) return f;
        throw new NotSupportedException($"field {typeName}.{name} not found");
    }

    // A `base` token's `_types` key: bases are normally stored OPEN (bare name), but an INNER generic class's base
    // carries its instantiation args (`AbstractList$IteratorImpl[gp:E]`, the nested-generic encoding) — strip them
    // for the emitted-type lookup (the constructed form is only needed at SetParent).
    static string BareTypeKey(string n)
    {
        var b = n.IndexOf('[');
        return b < 0 ? n : n[..b];
    }

    // name + parameter-type signature -> the overload key. `m` is a method DEF; the param types are STRUCTURED Type
    // nodes rendered to the m3 legacy sig-token spelling (the ONE documented legacy-string exception) so a DEF-side key
    // matches a CALL-side `sig` comma-string (kotc renders the call sig with the same legacyToken grammar; both are
    // bir2cir-lowered to the same vocabulary, so `int` == `int` / `gp:T` == `gp:T`).
    static string SigKey(string name, JsonElement methodDef) =>
        StripSigPrefixes(name + "(" + string.Join(",", methodDef.GetProperty("params").EnumerateArray().Select(p => SigTokenOf(p.GetProperty("type")))) + ")");

    static string SigKey(string name, string sig) => StripSigPrefixes(name + "(" + sig + ")");

    // The `clr:`/`clrg:` RESOLUTION-PREFIX vocabulary is a CALL-side spelling (bir2cir's legacy sig-token string) that
    // the structured `SigTokenOf` (DEF side, post type-flip) no longer emits — a bare FQN Fqn node produces
    // `System...IDictionary[gp:T,gp:T]`, not `clrg:System...IDictionary[...]`. That asymmetry made the exact SigKey
    // lookup miss (e.g. `map += pair`'s `plusAssign` on `IDictionary<K,V>`), so FindStatic fell through to an arbitrary
    // name-only overload (Atomics.plusAssign) -> type-mismatched IL -> InvalidProgramException. Strip the prefixes on
    // BOTH sides so def-key and call-sig agree. (`clr:` is not a substring of `clrg:`, so replacement order is moot.)
    static string StripSigPrefixes(string sig) => sig.Replace("clrg:", "").Replace("clr:", "");

    // The m3 legacy sig-token spelling of a type SLOT (structured Type node -> token; a legacy string passes verbatim).
    // Mirrors kotc.bir.TypeNode.legacyToken (a type var collapses to `gp:T`), so def-side and call-side sigs agree.
    static string SigTokenOf(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()
        : e.ValueKind == JsonValueKind.Object ? SigTokenOf(DotKt.Bir.TypeNode.Read(e))
        : "object";

    // The call node's `sig` — a STRUCTURED TypeNode array (#37 m3b) — rendered to the canonical overload-key token
    // string that the KEPT SigKey/SigTokenMatches machinery matches against a def's SigKey (both call- and def-side
    // walk the same structured TypeNodes via SigTokenOf, so the keys agree). Null when the node carries no `sig`
    // array; "" for an empty (nullary) sig (the existing sig.Length==0 -> argc 0 convention).
    static string SigString(JsonElement e) =>
        e.TryGetProperty("sig", out var s) && s.ValueKind == JsonValueKind.Array
            ? string.Join(",", s.EnumerateArray().Select(SigTokenOf))
            : null;

    static string SigTokenOf(DotKt.Bir.TypeNode t) => t switch
    {
        DotKt.Bir.TypeNode.Fqn f => f.Args == null ? f.Name : f.Name + "[" + string.Join(",", f.Args.Select(SigTokenOf)) + "]",
        DotKt.Bir.TypeNode.Tv => "gp:T",
        DotKt.Bir.TypeNode.Fn fn => (fn.Suspend ? "sfunc:" : "func:") + SigTokenOf(fn.Ret) + ":" + string.Join(",", fn.DelegateParams.Select(SigTokenOf)),
        DotKt.Bir.TypeNode.Nullable n => "nullable:" + SigTokenOf(n.Of),
        DotKt.Bir.TypeNode.Array a => "array:" + SigTokenOf(a.Elem),
        DotKt.Bir.TypeNode.ByRef b => "byref:" + SigTokenOf(b.Of),
        _ => "object",
    };

    // Substitute a type-scope type variable `Tv{type,i}` -> `args[i]` (the interface's instantiation), recursively.
    // Used to re-anchor an interface method's declared type (which names the INTERFACE's own params) to the
    // implementer's concrete args. A method-scope tv / an out-of-range index is left as-is.
    static DotKt.Bir.TypeNode SubstTv(DotKt.Bir.TypeNode t, DotKt.Bir.TypeNode[] args) => t switch
    {
        DotKt.Bir.TypeNode.Tv { Scope: "type" } tv when args != null && tv.I >= 0 && tv.I < args.Length => args[tv.I],
        DotKt.Bir.TypeNode.Fqn { Args: { } fa } f => new DotKt.Bir.TypeNode.Fqn(f.Name, fa.Select(a => SubstTv(a, args)).ToArray()),
        DotKt.Bir.TypeNode.Nullable n => new DotKt.Bir.TypeNode.Nullable(SubstTv(n.Of, args)),
        DotKt.Bir.TypeNode.Array a => new DotKt.Bir.TypeNode.Array(SubstTv(a.Elem, args)),
        DotKt.Bir.TypeNode.ByRef b => new DotKt.Bir.TypeNode.ByRef(SubstTv(b.Of, args)),
        DotKt.Bir.TypeNode.Fn fn => new DotKt.Bir.TypeNode.Fn(fn.Suspend, SubstTv(fn.Ret, args), fn.Params.Select(p => SubstTv(p, args)).ToArray(), fn.Recv == null ? null : SubstTv(fn.Recv, args)),
        _ => t,
    };

    // On an exact-sig MISS for a call that targets a GENERIC method: the call carries the INSTANTIATED arg types
    // (`array:object,object`) while the method is registered under its generic sig (`array:gp:T,gp:T`), so the exact
    // lookup fails and the name-only fallback returns the wrong (often primitive) overload. Prefer the UNIQUE generic
    // overload of that name — a non-generic overload would have matched exactly, so on a miss the generic one is the
    // intended target. Null if there are zero or several generic overloads (keep the existing fallback).
    MethodBuilder UniqueGenericOverload(TypeInfo ti, string name)
    {
        MethodBuilder cand = null;
        foreach (var kv in ti.MethodsBySig)
            if (kv.Key.StartsWith(name + "(", StringComparison.Ordinal) && _methodTypeParams.ContainsKey(kv.Value))
            {
                if (cand != null) return null;   // ambiguous: more than one generic overload
                cand = kv.Value;
            }
        return cand;
    }

    // Canonicalize the generic-parameter NAMES in a sig by their order of first appearance (`gp:E,gp:E` -> `gp:#0,gp:#0`;
    // `gp:K,gp:V` -> `gp:#0,gp:#1`). A method DEF names its OWN type parameter (`addAll<E>` -> `...[gp:E]`), but a CALL
    // from inside another generic names the same slot by the CALLER's parameter (`plus<T>` calling addAll -> `...[gp:T]`),
    // so the verbatim SigKey strings differ and MethodsBySig misses even though the overload is the intended one. The sig
    // SHAPE (which slot uses which type param, in which order) is identical across def and call — only the names differ —
    // so first-appearance ordinals make them agree, distinguishing `addAll(List,coll)` from `addAll(List,int,coll)`.
    static string NormalizeGpNames(string sig)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(sig, @"gp:([A-Za-z_][A-Za-z0-9_]*)", m =>
        {
            var nm = m.Groups[1].Value;
            if (!map.TryGetValue(nm, out var idx)) { idx = map.Count; map[nm] = idx; }
            return "gp:#" + idx;
        });
    }

    // The exact SigKey missed (a generic method whose sig mentions a `gp:` — def-side name != call-side name). Match by
    // the NAME-CANONICALIZED sig instead (first-appearance ordinals), returning the UNIQUE overload of `name` whose
    // normalized def-sig equals the normalized call-sig. Null on no/ambiguous match (keeps the existing fallbacks). Only
    // consulted after the exact lookup, so it never overrides a precise match — it recovers the cross-generic-rename case.
    MethodBuilder FindByNormalizedSig(TypeInfo ti, string name, string sig)
    {
        if (sig == null || !sig.Contains("gp:", StringComparison.Ordinal)) return null;
        var want = NormalizeGpNames(sig);
        var prefix = name + "(";
        MethodBuilder cand = null;
        foreach (var kv in ti.MethodsBySig)
        {
            if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || !kv.Key.EndsWith(")", StringComparison.Ordinal)) continue;
            var defSig = kv.Key.Substring(prefix.Length, kv.Key.Length - prefix.Length - 1);
            if (NormalizeGpNames(defSig) != want) continue;
            if (cand != null && !ReferenceEquals(cand, kv.Value)) return null;   // ambiguous
            cand = kv.Value;
        }
        return cand;
    }

    MethodInfo FindMethod(string typeName, string name, string sig = null)
    {
        typeName = NativeArrayOwner(typeName);
        var seenIfaces = new HashSet<string>();
        MethodBuilder FindInInterfaces(TypeInfo ti)
        {
            if (ti == null || !ti.Def.TryGetProperty("interfaces", out var ifs)) return null;
            foreach (var i in ifs.EnumerateArray())
            {
                if (ReadFqn(i) is not DotKt.Bir.TypeNode.Fqn iF) continue;
                var open = iF.Name;   // only the OPEN name matters here (avoid mapping a `[gp:T]` inner-generic arg)
                if (!_types.ContainsKey(open)) continue;   // referenced interfaces reflected at the FindMethod loop level
                if (!seenIfaces.Add(open) || !_types.TryGetValue(open, out var iti)) continue;
                if (sig != null && iti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
                if (sig != null && FindByNormalizedSig(iti, name, sig) is { } insm) return insm;
                if (sig != null && UniqueGenericOverload(iti, name) is { } igm) return igm;
                if (iti.Methods.TryGetValue(name, out var m)) return m;
                var inherited = FindInInterfaces(iti);
                if (inherited != null) return inherited;
            }
            return null;
        }
        // A type NOT in this assembly's `_types` is EXTERNAL — an rt-internal helper (`ClrCollectionDefaultsKt`,
        // referenced from an APP that links the rt via --ref). Resolve it by reflection on the loaded assembly instead
        // of indexing `_types` (which would KeyNotFound). (indexOf/listIterator/etc. lower to such helper callStatics.)
        if (!_types.ContainsKey(typeName))
        {
            // The owner is not emitted in THIS assembly -> a referenced .NET type. Resolve it with the prefix-aware
            // resolver (`ClrRef` strips `clr:`/`clrg:`/etc.; a bare FQN falls to reflection), then look the member up
            // including the reflected base-class + interface chain.
            Type ext = null;
            try { ext = ClrRef(typeName); } catch (NotSupportedException) { }
            // A bare OPEN generic Kotlin interface name (`kotlin.collections.Iterator`/`Map`, arrived via ParseOwner
            // stripping the `[gp:T]` args off `Iterator[gp:T]`.hasNext / `Map[gp:K,gp:V]`.get) has no reflection type
            // under its arity-less name — reflection knows it only as `Iterator`1`/`Map`2`. ResolveMethod then re-anchors
            // the returned OPEN member onto the constructed instantiation (TypeBuilder.GetMethod). Probe the arity suffix
            // and take the UNIQUE resolvable open definition (ambiguous bare name -> give up, keep the arity/null path).
            if (ext == null && !typeName.Contains('`'))
                for (int arity = 1; arity <= 8; arity++)
                    if (TryResolveType(typeName + "`" + arity) is { } cand)
                    {
                        if (ext != null) { ext = null; break; }   // ambiguous bare generic name
                        ext = cand;
                    }
            if (ext == null) return null;
            // A referenced file-class can carry several overloads that share name AND arity but differ in PARAM TYPES —
            // e.g. the stdlib's String-face `StringsKt.substring(String,int,int)` vs its CharSequence-face
            // `substring(dotkt$CharSequence,int,int)`, or `trim(String)` vs `trim(dotkt$CharSequence)`. Arity alone
            // then picks arbitrarily (reflection order) -> the wrong body runs (a String passed where the CharSequence
            // interface is expected -> EntryPointNotFound). Resolve by the FULL `sig` first (the same signature-keyed
            // disambiguation the in-`_types` path does via MethodsBySig); fall back to the arity pick on any miss.
            var extArgc = sig == null ? -1 : (sig.Length == 0 ? 0 : SplitTopLevel(sig).Count);
            return FindReflectedMethodBySig(ext, name, sig) ?? FindReflectedMethod(ext, name, extArgc);
        }
        // Walk this type's own members, then its EMITTED base/interface chain. If the base is NOT emitted here (an
        // external .NET base, e.g. an emitted class extending a BCL type), fall through to a reflected lookup on the
        // resolved base type so inherited .NET members are still found.
        for (var ti = _types[typeName]; ti != null; ti = ti.BaseName != null && _types.ContainsKey(BareTypeKey(ti.BaseName)) ? _types[BareTypeKey(ti.BaseName)] : null)
        {
            if (sig != null && ti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
            if (sig != null && FindByNormalizedSig(ti, name, sig) is { } nsm) return nsm;
            if (sig != null && UniqueGenericOverload(ti, name) is { } gm) return gm;
            if (ti.Methods.TryGetValue(name, out var m)) return m;
            var im = FindInInterfaces(ti);
            if (im != null) return im;
            // A REFERENCED (.NET) interface the emitted type implements (MutableList -> System...IList): reflect the
            // member on it — its get_Item/Add/… are real BCL slots the type binds but that live in no emitted `_types`.
            if (ti.Def.ValueKind == JsonValueKind.Object && ti.Def.TryGetProperty("interfaces", out var refIfs))
                foreach (var i in refIfs.EnumerateArray())
                    if (ReadFqn(i) is DotKt.Bir.TypeNode.Fqn rif && !_types.ContainsKey(rif.Name))
                    {
                        Type refIf = null; try { refIf = MapType(rif); } catch { }
                        if (refIf == null) continue;
                        // A constructed generic over an EMITTED param arg (IList<!0>) can't GetMethods() — reflect the
                        // OPEN definition's method, re-anchor it onto the constructed interface.
                        try { if (FindReflectedMethod(refIf, name) is { } rrm) return rrm; }
                        catch (NotSupportedException)
                        {
                            if (refIf.IsConstructedGenericType
                                && FindReflectedMethod(refIf.GetGenericTypeDefinition(), name) is { } om)
                                return TypeBuilder.GetMethod(refIf, om);
                        }
                    }
            // Base is an EXTERNAL (non-emitted) type -> inherited member must come from reflection on it. `ti.ClrBase`
            // is set when the base parsed to a `clr:`/`clrg:` type; otherwise resolve the base name on demand.
            if (ti.BaseName != null && !_types.ContainsKey(BareTypeKey(ti.BaseName)))
            {
                Type extBase = ti.ClrBase;
                if (extBase == null) { try { extBase = ClrRef(ti.BaseName); } catch (NotSupportedException) { } }
                if (extBase != null) { var rm = FindReflectedMethod(extBase, name); if (rm != null) return rm; }
            }
        }
        throw new NotSupportedException($"method {typeName}.{name} not found");
    }

    // Resolve a method by name on an already-RESOLVED (referenced .NET / baked) type, walking the standard CLR member
    // lookup: the type's own members + its base CLASS chain (reflection's `GetMethod` already includes inherited base
    // members for a class). Then the implemented-interface chain: `GetMethod`/`GetMethods` surface only a type's OWN
    // slots (an interface's own methods, a class's own+inherited-class methods) — NOT the interface methods a type
    // implements, whose class-side impl is a private explicit override (e.g. `dotkt$dimfwd$`) invisible by name — so
    // fall back to the transitively-implemented interfaces for BOTH a class and an interface receiver (`GetInterfaces`
    // returns the full flattened set for either). This is what lets an emitted class's inherited GENERIC interface
    // method (`AbstractCoroutineContextElement`'s `get<E>`) resolve at a `callInstance` call site. Pure CLR resolution;
    // no Kotlin/BCL name mapping. Null if absent.
    static MethodInfo FindReflectedMethod(Type t, string name, int argCount = -1)
    {
        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        // Arity-disambiguated lookup FIRST when the caller knows the parameter count: a referenced file-class can carry
        // overloads of the same name (e.g. _CollectionsKt.first(List<T>) vs first(Iterable<T>, predicate)); the
        // unconstrained GetMethod(name) below throws Ambiguous and an arbitrary pick mis-counts the stack.
        MethodInfo ByArity(Type ty) =>
            argCount < 0 ? null : ty.GetMethods(bf).FirstOrDefault(mm => mm.Name == name && mm.GetParameters().Length == argCount);
        if (ByArity(t) is { } am) return am;
        try { var m = t.GetMethod(name, bf); if (m != null) return m; }
        catch (AmbiguousMatchException) { var m = t.GetMethods(bf).FirstOrDefault(mm => mm.Name == name); if (m != null) return m; }
        // Implemented-interface fallback (last resort — the type's own surface is exhausted above): find an interface
        // method the type binds but implements via a name-invisible private explicit override.
        foreach (var bi in t.GetInterfaces())
        {
            if (ByArity(bi) is { } bam) return bam;
            try { var m = bi.GetMethod(name, bf); if (m != null) return m; }
            catch (AmbiguousMatchException) { var m = bi.GetMethods(bf).FirstOrDefault(mm => mm.Name == name); if (m != null) return m; }
        }
        return null;
    }

    // STRUCTURAL match for a sig token MapType could not resolve (an open generic-parameter token from the declared
    // callee's sig, e.g. `gp:T` / `array:gp:T` / `clrg:Collection[gp:T]`, unbound at a cross-module call site):
    // the token's SHAPE must agree with the candidate parameter's (generic param / array-of / constructed-generic).
    // Concrete tokens never reach here (MapType resolves them), so this cannot loosen an exact-type comparison.
    bool SigTokenMatchesOpen(string tok, Type p)
    {
        if (tok.StartsWith("byref:", StringComparison.Ordinal))
            return p.IsByRef && SigTokenMatches(tok.Substring(6), p.GetElementType());
        if (tok.StartsWith("array:", StringComparison.Ordinal))
            return p.IsArray && SigTokenMatches(tok.Substring(6), p.GetElementType());
        if (tok.StartsWith("nullable:", StringComparison.Ordinal))
            return SigTokenMatches(tok.Substring(9), p.IsGenericType && p.GetGenericTypeDefinition() == typeof(Nullable<>) ? p.GetGenericArguments()[0] : p);
        if (tok.StartsWith("gp:", StringComparison.Ordinal)) return p.IsGenericParameter || _sigConstructedOwner;
        // A constructed-generic owner token: either the legacy `clrg:Owner[args]` spelling OR — post #37 m3b type-path
        // structuring, where the CALL-side sig is rendered from structured TypeNodes via SigTokenOf which drops the
        // `clrg:` resolution prefix — a BARE `Owner[args]` (top-level `[`, no prefix-colon in the owner head; a `gp:`/
        // `func:` argument's colon sits AFTER the bracket). Both route to the generic-owner-definition match.
        {
            var genBr = tok.IndexOf('[');
            var isClrg = tok.StartsWith("clrg:", StringComparison.Ordinal);
            var firstColon = tok.IndexOf(':');
            var isBareGeneric = !isClrg && genBr > 0 && tok.EndsWith("]", StringComparison.Ordinal)
                && (firstColon < 0 || firstColon > genBr);
            if (!isClrg && !isBareGeneric) goto notGeneric;
            // Match on the generic-type-DEFINITION owner, not just "is a constructed generic": several same-arity
            // overloads (SequenceScope.yieldAll over Iterator<T> / IEnumerable<T> / Sequence<T>) all satisfy
            // IsGenericType, so the loose test binds an arbitrary one. The token's arg (`gp:T`) stays open, but its
            // OWNER (`System.Collections.Generic.IEnumerable`) still distinguishes IEnumerable<T> from Iterator<T>.
            if (!p.IsGenericType) return false;
            var body = isClrg ? tok.Substring(5) : tok;
            var br = body.IndexOf('[');
            var openName = br < 0 ? body : body.Substring(0, br);
            var argToks = br < 0 ? new List<string>() : SplitTopLevel(body.Substring(br + 1, body.Length - br - 2)).ToList();
            var def = TryResolveType(openName + "`" + argToks.Count);
            // Owner unresolvable (a Kotlin-only alias not in any referenced .NET assembly, e.g. `clrg:Collection[..]`
            // as a bare name) -> keep the OLD loose shape match rather than falsely reject (strictly additive).
            if (def == null) return true;
            if (!ReferenceEquals(p.GetGenericTypeDefinition(), def)) return false;
            // Recurse into the constructed generic's TYPE-ARGUMENTS. An open token arg (`gp:T`) must line up with a
            // generic-parameter position, so `IEnumerable[gp:T]` selects the GENERIC overload `maxOrNull<T>(IEnumerable<T>)`
            // and rejects the Double-specialized `maxOrNull(IEnumerable<Double>)` (whose arg is the concrete Double). A
            // concrete sub-token likewise requires the candidate's actual type-argument to equal it (via SigTokenMatches).
            var actualArgs = p.GetGenericArguments();
            for (var i = 0; i < argToks.Count && i < actualArgs.Length; i++)
                if (!SigTokenMatches(argToks[i], actualArgs[i])) return false;
            return true;
        }
        notGeneric:
        if (tok.StartsWith("func:", StringComparison.Ordinal))
        {
            // `func:<ret>:<arg1>,<arg2>,...` -> Func<arg1,...,argN,ret> (or Action<...> when ret==void). Match the
            // return type AND each parameter type structurally so overloads that differ ONLY by the selector's return
            // type stay distinguishable — e.g. `sumOf`'s Int/Long/Double/UInt/ULong family, where the loose "any Func"
            // test used to collapse them all onto the first-reflected (Double) overload -> wrong body / 0 result.
            if (!p.IsGenericType) return false;
            var rest = tok.Substring(5);
            var colon = FuncRetEnd(rest);
            var retTok = rest.Substring(0, colon);
            var argsPart = colon < rest.Length ? rest.Substring(colon + 1) : "";
            var argToks = argsPart.Length == 0 ? new List<string>() : SplitTopLevel(argsPart).ToList();
            var gargs = p.GetGenericArguments();
            if (retTok == "void")
            {
                if (gargs.Length != argToks.Count) return true;   // shape mismatch (Func vs Action) -> keep loose accept
                for (var i = 0; i < argToks.Count; i++)
                    if (!SigTokenMatches(argToks[i], gargs[i])) return false;
                return true;
            }
            if (gargs.Length != argToks.Count + 1) return true;   // shape mismatch -> keep loose accept
            for (var i = 0; i < argToks.Count; i++)
                if (!SigTokenMatches(argToks[i], gargs[i])) return false;
            return SigTokenMatches(retTok, gargs[gargs.Length - 1]);
        }
        return false;
    }

    // Combined sig-token match: a token MapType can fully resolve here (no unbound `gp:`) must EQUAL the candidate's
    // type exactly; an unresolvable token falls to the structural open-token shape match. Used to recurse into a
    // constructed-generic type-argument or a func's return/param slot, so a concrete inner token (a func's Int-vs-Double
    // return, `IEnumerable[Double]` vs `[gp:T]`) is still discriminating instead of collapsing onto the loose shape.
    bool SigTokenMatches(string tok, Type p)
    {
        // A token mentioning an unbound `gp:` is inherently OPEN — compare by SHAPE. (MapType would either throw or,
        // for a bare `gp:T`, resolve it to a placeholder Type that never ReferenceEquals the candidate's actual generic
        // parameter, wrongly rejecting the right overload -> arity fallback picks the wrong one. e.g. yieldAll's
        // IEnumerable<T> overload lost to the first-reflected Iterator<T>.) Only a fully-concrete token uses MapType.
        if (!tok.Contains("gp:", StringComparison.Ordinal))
        {
            Type want; try { want = MapType(tok); } catch { want = null; }
            if (want != null) return want == p;
        }
        return SigTokenMatchesOpen(tok, p);
    }

    // Sig-aware overload pick on a REFERENCED file-class: several methods can share name+arity but differ in PARAM
    // TYPES (a String-face vs a `dotkt$CharSequence`-face stdlib extension). Map each `sig` token to its Type and
    // require an EXACT full match against a reflected overload's parameters; return the UNIQUE match, or null on a
    // miss/ambiguity so the caller falls back to the arity pick (never a regression — this only ADDS disambiguation).
    // A token that MapType can't resolve here (an unbound `gp:T`, a not-yet-emitted type) yields no match -> fall back.
    MethodInfo FindReflectedMethodBySig(Type ext, string name, string sig)
    {
        if (sig == null) return null;
        var toks = sig.Length == 0 ? new List<string>() : SplitTopLevel(sig).ToList();
        var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        // When `ext` is a CONSTRUCTED generic (SequenceScope<String>), its methods' params reflect the instantiation
        // (IEnumerable<String>, not IEnumerable<T>) — so a `gp:T` token, which is the caller's OWN open param bound by
        // that same instantiation, must match the concrete arg. When `ext` is OPEN/non-generic (the static _CollectionsKt),
        // a `gp:T` token discriminates the method-generic overload (`maxOrNull<T>(IEnumerable<T>)`) from a concrete
        // sibling (`maxOrNull(IEnumerable<Double>)`), so it must require a genuine generic-parameter arg.
        _sigConstructedOwner = ext.IsConstructedGenericType;
        MethodInfo match = null;
        foreach (var m in ext.GetMethods(bf))
        {
            if (m.Name != name) continue;
            var ps = m.GetParameters();
            if (ps.Length != toks.Count) continue;
            var ok = true;
            for (var i = 0; i < ps.Length; i++)
                // SigTokenMatches is the combined matcher: a fully-CONCRETE token (no `gp:`) requires an EXACT type
                // (so a String-face overload isn't confused with a CharSequence-face one), while ANY token mentioning
                // `gp:` is compared STRUCTURALLY — even when it happens to resolve here. That last point is essential:
                // a call from INSIDE a generic method (`fun <T> mx(c) = c.maxOrNull()`) carries `sig=IEnumerable[gp:T]`
                // where `gp:T` resolves to the CALLER's own T builder; an exact compare against the callee's OWN `T`
                // never matches, dropping to the arity fallback which arbitrarily picks a specialized sibling
                // (`maxOrNull(IEnumerable<Double>)`). The structural path selects the generic `maxOrNull<T>(IEnumerable<T>)`
                // in both the generic-caller and the non-generic-caller case. (Mirrors the in-`_types` MethodsBySig keys.)
                if (!SigTokenMatches(toks[i], ps[i].ParameterType)) { ok = false; break; }
            if (!ok) continue;
            if (match != null)
            {
                // Two methods matching the SAME sig token necessarily have identical parameter types (each was
                // checked against the same MapType(toks)). A genuine overload set can't collide here — a distinct
                // overload has a distinct sig. So a second exact match is a DUPLICATE method emission (the stdlib
                // expect/actual fileClass merge can emit a top-level fn twice, e.g. `_ArraysKt.sum(int[])` x2) — NOT
                // a real ambiguity. Keeping the first is correct (the bodies are identical); returning null here
                // would drop to the arity fallback and pick the wrong same-arity overload (sum(int[]) -> sum(sbyte[])).
                continue;
            }
            match = m;
        }
        return match;
    }

    MethodBuilder FindStatic(string name, string sig = null)
    {
        if (sig != null)
            foreach (var ti in _types.Values)
                if (ti.IsFileClass && ti.MethodsBySig.TryGetValue(SigKey(name, sig), out var ms)) return ms;
        // exact-sig miss on a generic method whose sig carries a `gp:` — the DEF names its own type param but the CALL
        // names it by the caller's (a top-level static called from inside a generic): match by the name-canonicalized sig.
        if (sig != null)
            foreach (var ti in _types.Values)
                if (ti.IsFileClass && FindByNormalizedSig(ti, name, sig) is { } nsm) return nsm;
        // exact-sig miss with a generic target -> the unique generic overload (its instantiated call sig won't match).
        if (sig != null)
            foreach (var ti in _types.Values)
                if (ti.IsFileClass && UniqueGenericOverload(ti, name) is { } gm) return gm;
        foreach (var ti in _types.Values)
            if (ti.IsFileClass && ti.Methods.TryGetValue(name, out var mb)) return mb;
        throw new NotSupportedException("static method not found: " + name);
    }

    // The `(object, IntPtr)` delegate constructor. For a delegate type instantiated with a user TypeBuilder
    // (e.g. `Func<int, Point>` where Point is still being emitted), plain reflection `GetConstructor` throws
    // ("generic instantiation does not support resolving members"); TypeBuilder.GetConstructor bridges it. This
    // unblocks delegates/refs whose signature mentions a user type (`::Ctor`, unbound `Class::method`, lambdas
    // returning a user class).
    static bool ContainsTypeBuilder(Type t)
    {
        // `IsGenericParameter` (not just `is GenericTypeParameterBuilder`): the new Reflection.Emit hands generic
        // params read back off a constructed type (via GetElementType/GetGenericArguments) as a normalized param type
        // that fails the `is`-check yet still needs TypeBuilder.GetX to encode (e.g. `Func<E[]>` in a generic method).
        if (t is TypeBuilder || t is GenericTypeParameterBuilder || t.IsGenericParameter) return true;
        if (t.HasElementType) return ContainsTypeBuilder(t.GetElementType());
        // `GetGenericArguments().Length > 0`, NOT `IsGenericType`: the new Reflection.Emit reports IsGenericType=false
        // for a TypeBuilderInstantiation (a runtime-def generic like `Func<E[]>` instantiated over builder args), which
        // would skip the recursion that finds the nested builder/param.
        if (!t.IsGenericParameter && t.GetGenericArguments().Length > 0)
        {
            // A CONSTRUCTED generic whose open definition is a TypeBuilder (e.g. `Iterator<int>` while Iterator is being
            // emitted) is a TypeBuilderInstantiation: resolving its members needs TypeBuilder.GetX, yet it is not itself
            // `is TypeBuilder`. Detect it via its definition (catches e.g. `Func<Iterator<int>>` through recursion).
            if (t.GetGenericTypeDefinition() is TypeBuilder) return true;
            foreach (var a in t.GetGenericArguments()) if (ContainsTypeBuilder(a)) return true;
        }
        return false;
    }

    static bool IsTypeBuilderBackedGeneric(Type t) =>
        IsGenericInst(t) && t.GetGenericTypeDefinition() is TypeBuilder;

    // Is `t` a delegate type? A TypeBuilderInstantiation of a BAKED generic delegate (`Func<Res,int>`, Res a user
    // TypeBuilder) reports `typeof(Delegate).IsAssignableFrom` UNRELIABLY (its base chain is not resolvable pre-bake),
    // so fall back to testing its baked generic DEFINITION (`System.Func`2`). A synthetic (TypeBuilder-def) delegate
    // stays on the direct assignability check (its own MulticastDelegate base is set at DefineType).
    static bool IsDelegateType(Type t)
    {
        if (typeof(System.Delegate).IsAssignableFrom(t)) return true;
        // Guard HasElementType/IsGenericParameter BEFORE probing generics: an array/byref/pointer (a Reflection.Emit
        // SymbolType) or a generic-param `want` (a) is never a delegate and (b) throws NotSupportedException on
        // GetGenericArguments (the base-Type default) — the same traversal quirk ContainsTypeBuilder guards. The old
        // direct-assignability-first guard never hit this because it short-circuited on non-delegates.
        if (t.HasElementType || t.IsGenericParameter) return false;
        return IsGenericInst(t) && t.GetGenericTypeDefinition() is { } def && def is not TypeBuilder
            && typeof(System.Delegate).IsAssignableFrom(def);
    }

    // A subset of ContainsTypeBuilder: only an OPEN generic PARAMETER (`!T`), NOT a concrete TypeBuilder CLASS arg.
    // The delegate-arg rewrap (EmitArg case 4) can build a target delegate whose type-arg is a user TypeBuilder class
    // (`Func<Res,int>` — DelegateCtor/InvokeOf bridge it via TypeBuilder.GetX), but NOT one still mentioning an open
    // generic param (no concrete ctor to bind); this predicate distinguishes the two so the guard rewraps the former.
    static bool ContainsGenericParameter(Type t)
    {
        if (t.IsGenericParameter) return true;
        if (t.HasElementType) return ContainsGenericParameter(t.GetElementType());
        if (!t.IsGenericParameter && t.GetGenericArguments().Length > 0)
            foreach (var a in t.GetGenericArguments()) if (ContainsGenericParameter(a)) return true;
        return false;
    }

    // ---- BCL interop (@Clr) via reflection ----
    // A shared compiler-synthetic type that, once verified cross-assembly, is emitted ONCE (public) in the rt stdlib
    // dll and REFERENCED by app assemblies instead of re-synthesized per-assembly (canonicalization), so a value
    // crossing the app<->rt boundary keeps ONE CLR identity. CharSequence first; extend as each synthetic is verified.
    // (`dotkt$KProperty`/`dotkt$KPropertyImpl` — formerly listed here — are RETIRED, #70: `kotlin.reflect.KProperty*`
    // is now a REAL rt-stdlib interface and `kotlin.reflect.ClrPropertyStub` a REAL rt-stdlib class, both referenced
    // — not re-synthesized per-assembly — so no canonicalization is needed for either.)
    static readonly HashSet<string> CanonicalSynthetics = new(StringComparer.Ordinal)
        { "dotkt$CharSequence" };

    // #68: stamp the STANDARD [System.Runtime.CompilerServices.CompilerGenerated] on a compiler-generated type — the
    // generated signal read from the `generated:true` BIR flag (and applied to ilemit's OWN synthetics too), so facadegen
    // skips generated types by attribute rather than by `dotkt$` name-sniffing.
    internal static void StampCompilerGenerated(TypeBuilder tb) =>
        tb.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));

    // #68: same stamp for an ilemit-authored generated METHOD (the covar/dim* variance-bridge synthetics, the reverse-
    // enumerator adapter's own methods) — one consistent `dotkt$` + [CompilerGenerated] marking for every synthetic member.
    internal static void StampCompilerGenerated(MethodBuilder mb) =>
        mb.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute).GetConstructor(Type.EmptyTypes), new object[0]));

    // True when `name` is already defined by an exact --runtime-refs assembly. The module under
    // construction is a PersistedAssemblyBuilder (not a loaded AppDomain assembly), so it never self-matches.
    static bool ResolvesExternally(string name) =>
        RuntimeReferences.Assemblies.Any(a => { try { return a.GetType(name) != null; } catch { return false; } });

    static readonly Dictionary<string, Type> _typeCache = new();

    static Type ResolveType(string name)
    {
        if (_typeCache.TryGetValue(name, out var c)) return c;
        // Precedence: (1) the app's resolved runtime catalog is AUTHORITATIVE — it wins even over a TPA-named
        // assembly so an app that pins a version emits against it; (2) ilemit's host core (CoreLib/mscorlib types);
        // (3) a TPA fallback for framework/inbox types the app does not copy-local (System.Text.Json, System.Net.Http,
        // System.Text.RegularExpressions, System.Console, …).  No hardcoded per-assembly probe list — (3) scans TPA.
        var t = RuntimeReferences.ResolveType(name)
            ?? Type.GetType(name)
            ?? RuntimeReferences.ResolveFromHostFramework(name);
        // A dotted FQN may denote a NESTED type: CLR metadata separates nesting with '+' (Outer+Inner) while the
        // producer's spec is dotted (`kotlin.time.Clock.System` -> `kotlin.time.Clock+System`). Probe by replacing
        // the LAST '.' with '+' and re-resolving; the recursion (via TryResolveType) walks deeper nesting levels
        // (a.b.C.D -> a.b.C+D -> a.b+C+D). Pure CLR name resolution — no source-language knowledge.
        if (t == null)
        {
            var dot = name.LastIndexOf('.');
            if (dot > 0) t = TryResolveType(name[..dot] + "+" + name[(dot + 1)..]);
        }
        if (t == null) throw new NotSupportedException("cannot resolve .NET type " + name);
        _typeCache[name] = t;
        return t;
    }

    // A property getter/setter on a (possibly emitted-generic-instantiated) type. On a constructed generic type
    // whose arg is an emitted generic parameter (TypeBuilderInstantiation), runtime reflection refuses
    // GetProperty — re-anchor the open definition's accessor onto the constructed type via TypeBuilder.GetMethod.
    static MethodInfo PropAccessor(Type type, string name, bool getter)
    {
        try { var pi = type.GetProperty(name); var m = getter ? pi?.GetGetMethod() : pi?.GetSetMethod(); if (m != null) return m; }
        catch (NotSupportedException) { }
        // A non-generic type with the property on a base class (e.g. an Element's inherited members) — walk up.
        if (!type.IsGenericType)
        {
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                var pi = b.GetProperty(name); var m = getter ? pi?.GetGetMethod() : pi?.GetSetMethod();
                if (m != null) return m;
            }
        }
        var open = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var openPi = open.GetProperty(name);
        if (openPi != null) return TypeBuilder.GetMethod(type, getter ? openPi.GetGetMethod() : openPi.GetSetMethod());
        // Inherited interface property (`ICollection<T>.Count` accessed on `IList<T>`, or the ARITY-CHANGING
        // `IReadOnlyCollection<KeyValuePair<K,V>>.Count` accessed on `IReadOnlyDictionary<K,V>`/`IDictionary<K,V>`):
        // interface GetProperty doesn't traverse base interfaces, so walk them, substitute the open def's type
        // parameters into the (possibly constructed-arg) base reference, and re-anchor (the accessor twin of the
        // base-interface walk in FindMethod's FindInInterfaces).
        var typeArgs = type.GetGenericArguments();
        foreach (var bi in open.GetInterfaces())
        {
            var biOpen = bi.IsGenericType ? bi.GetGenericTypeDefinition() : bi;
            var bp = biOpen.GetProperty(name);
            var acc = getter ? bp?.GetGetMethod() : bp?.GetSetMethod();
            if (acc == null) continue;
            if (!bi.IsGenericType) return acc;
            var biCon = SubstituteIfaceArgs(bi, typeArgs);
            return IsTbInstantiation(biCon) ? TypeBuilder.GetMethod(biCon, acc)
                : (getter ? biCon.GetProperty(name).GetGetMethod() : biCon.GetProperty(name).GetSetMethod());
        }
        return null;
    }

    // List a .NET type's public property names (instance+static, incl. inherited) — for an actionable "no such
    // property" diagnostic instead of a bare NullReferenceException.
    static string PropList(Type t)
    {
        try { return string.Join(", ", t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(p => p.Name).Distinct().OrderBy(s => s)); }
        catch { return "?"; }
    }

    // W1-S1 (#46/#44) — CONSUME-ONLY generic-method linking. bir2cir holds the winning MethodInfo (ReferenceMetadataIndex
    // / MetadataLoadContext) and carries the FIR-resolved reference as the `memberSig` descriptor: the callee's DECLARED
    // parameter types (OPEN — a method type-var is `{t:tv,scope:method}`, a constructed generic keeps its args). ilemit
    // resolves the owner, enumerates the name-matching generic-method DEFINITIONS of the right generic-arity + param-count,
    // and matches each declared param STRUCTURALLY under positional-tv equality. It requires EXACTLY ONE hit — 0 is a hard
    // ABI-mismatch error, >1 a malformed-descriptor error, each printing the full descriptor. NO shape-string, no
    // name/arity first-pick, no assignability scoring: ilemit makes no overload choice (the retired ResolveGenericMethod
    // shapes-match `?? cands.First()` and its `Shape(Type)` helper are deleted).
    MethodInfo ResolveGenericMethod(Type type, string name, Type[] typeArgs, JsonElement e, bool instance = false)
    {
        if (!e.TryGetProperty("memberSig", out var sigEl) || sigEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"ilemit: clrGeneric* call to {type?.FullName}.{name}<{typeArgs.Length}> is missing its `memberSig` descriptor " +
                "(bir2cir must carry the FIR-resolved parameter signature — W1-S1 #46)");
        var declParams = sigEl.EnumerateArray().Select(DotKt.Bir.TypeNode.Read).ToArray();
        // A generic INSTANCE method reflected off a CONSTRUCTED receiver (`Box<Int>`) has its enclosing-type type-vars
        // already SUBSTITUTED on each candidate param (`Func<Int32,R>`), while memberSig carries the OPEN `tv(type,i)`.
        // Resolve a type-scope tv against the constructed owner's type args so the two line up.
        var ownerArgs = type != null && type.IsGenericType ? type.GetGenericArguments() : null;
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static);
        var cands = type.GetMethods(flags)
            .Where(m => m.Name == name && m.IsGenericMethodDefinition
                     && m.GetGenericArguments().Length == typeArgs.Length
                     && m.GetParameters().Length == declParams.Length)
            .ToList();
        var hits = cands
            .Where(m => m.GetParameters().Select((p, i) => GenericParamMatches(declParams[i], p.ParameterType, ownerArgs)).All(x => x))
            .ToList();
        if (hits.Count == 1) return hits[0].MakeGenericMethod(typeArgs);
        var desc = $"{type?.FullName}.{name}<{typeArgs.Length}>{sigEl}";
        if (hits.Count == 0)
            throw new InvalidOperationException(
                $"ilemit: no {(instance ? "instance" : "static")} generic method matches the resolved descriptor {desc} " +
                $"(ABI mismatch; {cands.Count} same-name/arity/param-count candidate(s): {string.Join("; ", cands.Select(m => m.ToString()))})");
        throw new InvalidOperationException(
            $"ilemit: resolved generic descriptor {desc} is AMBIGUOUS — {hits.Count} methods match (malformed memberSig): " +
            string.Join("; ", hits.Select(m => m.ToString())));
    }

    // True iff a structured TypeNode mentions a type variable anywhere — the split between a fully-CONCRETE declared
    // param (resolvable by MapType, matched by exact identity) and a var-bearing one (matched structurally, positional).
    static bool ContainsTv(DotKt.Bir.TypeNode t) => t switch
    {
        DotKt.Bir.TypeNode.Tv => true,
        DotKt.Bir.TypeNode.Array a => ContainsTv(a.Elem),
        DotKt.Bir.TypeNode.ByRef b => ContainsTv(b.Of),
        DotKt.Bir.TypeNode.Nullable n => ContainsTv(n.Of),
        DotKt.Bir.TypeNode.Oblivious o => ContainsTv(o.Of),
        DotKt.Bir.TypeNode.Fn fn => ContainsTv(fn.Ret) || fn.DelegateParams.Any(ContainsTv),
        DotKt.Bir.TypeNode.Fqn f => f.Args != null && f.Args.Any(ContainsTv),
        _ => false,
    };

    // Structural equality between a carried (CLR-lowered) DECLARED parameter TypeNode and the parameter type of a
    // candidate generic-method definition, under positional type-variable equality. A fully-concrete node resolves via
    // MapType and requires EXACT identity (no assignability scoring); a var-bearing node recurses structurally so a
    // `{t:tv,scope:method,i}` matches the candidate's i-th method generic parameter (`Box<T>` matches `Box`1[!!0]`).
    // `ownerArgs` = the constructed receiver's type args (null when the owner is non-generic / an open def): a
    // `{t:tv,scope:type,i}` matches whatever those args substituted into the candidate's param at that position.
    bool GenericParamMatches(DotKt.Bir.TypeNode node, Type p, Type[] ownerArgs)
    {
        // A function-type node ALWAYS matches structurally (even fully concrete): MapType(fn) builds ONE specific
        // delegate (a BCL `Action<Panel>`), but the reflected param may be a per-assembly SYNTHETIC `KAction<Panel>` a
        // DotKt library baked — GenericDelegateMatches accepts BOTH BCL Func/Action AND synthetic KFunc/KAction by shape.
        if (node is DotKt.Bir.TypeNode.Fn fnNode) return GenericDelegateMatches(fnNode, p, ownerArgs);
        if (!ContainsTv(node))
        {
            try { return MapType(node) == p; } catch { return false; }
        }
        switch (node)
        {
            case DotKt.Bir.TypeNode.Tv { Scope: "type" } ttv:
                // The enclosing-type type-var. On a CONSTRUCTED owner it is already substituted on the candidate param
                // (compare against the owner's i-th arg); on an open owner/def it stays a GenericTypeParameter (positional).
                if (ownerArgs != null && ttv.I >= 0 && ttv.I < ownerArgs.Length) return ownerArgs[ttv.I] == p;
                return p.IsGenericParameter && p.GenericParameterPosition == ttv.I && p.DeclaringMethod == null;
            case DotKt.Bir.TypeNode.Tv mtv:   // scope "method"
                return p.IsGenericParameter && p.GenericParameterPosition == mtv.I && p.DeclaringMethod != null;
            case DotKt.Bir.TypeNode.Array a:
                return p.IsArray && GenericParamMatches(a.Elem, p.GetElementType(), ownerArgs);
            case DotKt.Bir.TypeNode.ByRef b:
                return p.IsByRef && GenericParamMatches(b.Of, p.GetElementType(), ownerArgs);
            case DotKt.Bir.TypeNode.Nullable n:
                return p.IsGenericType && p.GetGenericTypeDefinition() == typeof(Nullable<>)
                       && GenericParamMatches(n.Of, p.GetGenericArguments()[0], ownerArgs);
            case DotKt.Bir.TypeNode.Oblivious o:
                return GenericParamMatches(o.Of, p, ownerArgs);
            case DotKt.Bir.TypeNode.Fn fn:
                return GenericDelegateMatches(fn, p, ownerArgs);
            case DotKt.Bir.TypeNode.Fqn f:   // f.Args != null (ContainsTv implies a var-bearing arg)
                if (!p.IsGenericType) return false;
                var pa = p.GetGenericArguments();
                if (pa.Length != f.Args.Length) return false;
                Type candDef; try { candDef = p.GetGenericTypeDefinition(); } catch { return false; }
                if (SameGenericDef(f.Name, f.Args.Length) != candDef) return false;
                for (int i = 0; i < pa.Length; i++) if (!GenericParamMatches(f.Args[i], pa[i], ownerArgs)) return false;
                return true;
            default:
                return false;
        }
    }

    // The generic type DEFINITION of a carried (CLR-lowered) name: an emitted open type (this assembly) or a referenced
    // .NET generic by arity-suffixed FQN. Null when unresolvable (a non-match, never a silent object-degrade).
    Type SameGenericDef(string name, int arity)
    {
        if (_types.TryGetValue(name, out var oti)) return oti.AsType;
        try { return ResolveType(name.Contains('`') ? name : name + "`" + arity); } catch { return null; }
    }

    // A var-bearing function-type declared param (`Func<T,R>` / `Action<T>`) against the candidate's delegate param.
    // The candidate may be a BCL `System.Func`/`Action` OR the synthetic `KFunc`/`KAction` a DotKt library bakes when
    // the function type is over generic user types (unencodable as a BCL delegate) — both shape-match identically
    // (KFunc/KAction mirror Func/Action's generic-arg layout: params[..] + ret for Func-like, params[..] for Action-like).
    bool GenericDelegateMatches(DotKt.Bir.TypeNode.Fn fn, Type p, Type[] ownerArgs)
    {
        var dp = fn.DelegateParams;
        bool isVoid = fn.Ret is DotKt.Bir.TypeNode.Fqn { Name: "void" or "System.Void", Args: null };
        // The NON-generic `System.Action` (0-arg, void) — a `()->Unit` lambda param (`column(setup, content: ()->Unit)`).
        if (!p.IsGenericType) return isVoid && dp.Length == 0 && p.Name == "Action" && p.Namespace == "System";
        var dn = p.GetGenericTypeDefinition().Name;
        var ga = p.GetGenericArguments();
        if (isVoid)
        {
            if (!(dn.StartsWith("Action`", StringComparison.Ordinal) || dn.StartsWith("KAction`", StringComparison.Ordinal))
                || ga.Length != dp.Length) return false;
            for (int i = 0; i < dp.Length; i++) if (!GenericParamMatches(dp[i], ga[i], ownerArgs)) return false;
            return true;
        }
        if (!(dn.StartsWith("Func`", StringComparison.Ordinal) || dn.StartsWith("KFunc`", StringComparison.Ordinal))
            || ga.Length != dp.Length + 1) return false;
        for (int i = 0; i < dp.Length; i++) if (!GenericParamMatches(dp[i], ga[i], ownerArgs)) return false;
        return GenericParamMatches(fn.Ret, ga[^1], ownerArgs);
    }

}
