using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotKt.Bir;

// .NET-INTEROP CALL BINDING (A2 / #61): the Kotlin<->CLR binding for a facadegen-injected .NET member call. kotc
// emits a PLAIN `callStatic`/`callInstance` by the .NET owner's FQN IDENTITY (`callStatic Kfc.App.get_Count`,
// `callInstance System.Text.StringBuilder.Append`) carrying only frontend FACTS — static-ness (callStatic vs
// callInstance), the accessor name (`get_X`/`set_X`), `typeArgs`, the `op_` name with the receiver already
// prepended, the constructed-generic owner IDENTITY (memberType supertype walk) — and does NOT decide the .NET call
// SHAPE. THIS pass resolves the owner FQN against the loaded .NET reference assemblies (ReferenceMetadataIndex's
// long-lived MetadataLoadContext) and, when it IS a reachable .NET type, reflects the member to bind the shape:
// static/instance method -> `clrStatic`/`clrInstance`; a `get_X`/`set_X` naming a .NET property OR field ->
// `clrPropGet`/`clrPropSet`; a generic method (`typeArgs` present) -> `clrGenericStatic`/`clrGenericInstance`; an
// indexer (`get_Item`/`set_Item`, an indexed property) or a synthetic member-extension accessor (no matching
// property/field) stays a plain instance method call. A `kotlin.*`/local/unresolvable owner is left untouched (the
// stdlib is bound by MemberCallSubstitution off the ref.dll; a local type is emitted here). CLR-ONLY vocabulary that
// has no plain-Kotlin form — `.NET events` (ClrEvent<T>), `byref`/`ClrRef<T>` — is NOT emitted as a plain call by
// kotc (kotc lowers it directly, as facadegen-injected CLR vocab), so it never reaches this pass. Runs BEFORE
// ClrEventSubscriptionBinding/KClassMemberBinding/MemberCallSubstitution and before BirTypeLowering, so the shaped `clr*`
// nodes still carry pure-Kotlin type tokens that the subsequent lowering turns into the CLR forms — the CIR is
// byte-identical to what kotc used to emit directly (the shape decision merely moved down a layer). Bottom-up walk,
// mirroring ClrEventSubscriptionBinding/KClassMemberBinding.
static class NetInteropBinding
{
    static ReferenceMetadataIndex _refs;

    // Mutates IN PLACE (like ShapeSynthesis): this runs in bir2cir's phase-1 per-file region where every pass edits
    // `bir.Root` in place (BirFile.Root is init-only, not reassignable). The node identity is preserved (its parent link
    // stays valid); only its `k` + field set change from a plain call to the CLR shape.
    public static void Apply(JsonNode root, ReferenceMetadataIndex refs) { _refs = refs; Walk(root); }

    static string Str(JsonNode n) => (n as JsonValue)?.GetValue<string>();

    static void Walk(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var kv in obj.ToList()) if (kv.Value != null) Walk(kv.Value);   // children first (bottom-up)
            Reshape(obj);
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr) if (item != null) Walk(item);
        }
    }

    static void Reshape(JsonObject node)
    {
        var k = Str(node["k"]);
        // #73 M4-b: a FIELD read/write on a facadegen-injected .NET owner. kotc emits a plain `field`/`setField` by the
        // .NET-FQN identity (no shape decision); the .NET member SHAPE is bound HERE — the same axis #61 used for calls.
        // A `field`/`setField` whose owner resolves to a .NET type declaring a property OR field of that name (both, via
        // MemberIsPropertyOrField) -> clrPropGet/clrPropSet, whose EmitClrPropGet/Set is struct-receiver-safe + inlines a
        // const field (unlike the plain-field external Ldfld/Callvirt route) — matching the old kotc clrPropGet parity,
        // which reshaped unconditionally. A member the refs can't see (a non-.NET owner, or a name absent from the .NET
        // type) never resolves here -> the plain `field`/`setField` is left for ilemit's own handler.
        if (k == "field" || k == "setField") { ReshapeField(node, write: k == "setField"); return; }
        // #73 M4.4: a BOUND method reference `netObj::m`. kotc emits a NEUTRAL `newBoundDelegate` (the same kind it uses
        // for a Kotlin-owner bound ref) carrying the owner FQN identity + argTypes; bir2cir decides the SHAPE. When the
        // owner resolves to a .NET type off the refs -> the CLR bound-delegate dialect node `newBoundClrDelegate` (ilemit
        // binds the target by reflection). A Kotlin/local owner never resolves here -> the plain `newBoundDelegate` is
        // left for ilemit's own FindMethod-based handler. Byte-identical to kotc's former newBoundClrDelegate emit.
        if (k == "newBoundDelegate") { ReshapeBoundDelegate(node); return; }
        // #178: the options-taking `kotlin.text.Regex` constructors. A plain `new` on a @ClrTypeAlias owner is bound to
        // newClr by MemberCallSubstitution — but `Regex(String, RegexOption)` / `Regex(String, Set<RegexOption>)` map to
        // the BCL `Regex(String, RegexOptions)`, and the DotKt enum / set neither RESOLVES to nor carries the numeric
        // value of the `[Flags] System...RegexOptions` int param. Convert the option arg to the RegexOptions bitmask HERE
        // (there is no pure-Kotlin ctor body to do it — the expect fixes options to the ctor). A no-op for every other
        // `new`, which flows to MemberCallSubstitution.TransformNew unchanged.
        if (k == "new") { ReshapeRegexCtorOptions(node); return; }
        if (k != "callStatic" && k != "callInstance") return;
        // dll2klib's metadata-only await declaration deliberately has no CLR
        // member to bind. Preserve the Kotlin call and its marker until
        // SuspendColdLowering resolves the awaiter pattern from the refs.
        if (node["clrAwaitBridge"]?.GetValue<bool>() == true) return;
        var ownerJson = node["ownerType"];
        // Peel Nullable/Oblivious/ByRef wrappers to reach the underlying .NET Fqn (a `List<Item>?` receiver's owner is
        // spelled `nullable(fqn List<Item>)`); the ORIGINAL wrapped node is preserved verbatim in the `type` slot below
        // (ilemit unwraps nullability when resolving the owner — byte-identical to the old kotc `clrInstance.type`).
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var bare = ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name);
        var method = Str(node["method"]);
        var netType = _refs.ResolveNetType(bare, ownerFqnNode.Args?.Length ?? 0);
        Type dotKtEmittedType = null;
        if (netType == null && _refs.HasDotKtOwner(bare))
            dotKtEmittedType = _refs.ResolveRefType(bare, ownerFqnNode.Args?.Length ?? 0);
        // A basic Kotlin enum is emitted as a real CLR value-type enum. Its constants, inherited System.Enum/Object
        // members, value receiver address-taking, and constrained dispatch are therefore ordinary CLR ABI facts even
        // though the declaration came from Kotlin. Route that entire owner shape through this binder; rich Kotlin enums
        // remain class-like DotKt owners and stay on the Kotlin ABI path.
        if (netType == null && dotKtEmittedType?.IsEnum == true)
            netType = dotKtEmittedType;
        // A referenced DotKt owner normally stays on the Kotlin ABI path: reflecting every Kotlin member as raw CLR
        // would erase property/operator conventions that MemberCallSubstitution owns. One language ABI seam genuinely
        // needs the emitted metadata, though: `Comparable<T>.compareTo` implements CLR IComparable<T>.CompareTo and the
        // exported slot is therefore PascalCase. Admit ONLY that structurally-proven seam to the existing binder. A
        // standalone Kotlin `operator fun compareTo` still declares lowercase `compareTo` and does not match, while an
        // arbitrary package/type name is irrelevant. This keeps the decision in bir2cir and leaves ilemit an exact CIR
        // method name, without reclassifying the rest of a referenced Kotlin library as C#.
        if (netType == null && k == "callInstance" && method == "compareTo"
            && dotKtEmittedType is Type dotKtComparable
            && !DeclaresPublicMethodNamed(dotKtComparable, "compareTo")
            && DeclaresPublicMethodNamed(dotKtComparable, "CompareTo")
            && ImplementsGenericIComparable(dotKtComparable))
            netType = dotKtComparable;
        if (netType == null) return;   // not a reachable .NET-interop owner -> leave for the other binders

        // kotc preserves Kotlin object/companion semantics in BIR: a surfaced static member can be a callInstance whose
        // receiver is the synthetic `Owner.INSTANCE`. Resolve the actual property/field declaration's static bit here
        // (notably a referenced Kotlin enum constant is a CLR static field). Static classes remain the member-method
        // counterpart. A DotKt-emitted Kotlin object does not enter this .NET binder, so its INSTANCE receiver remains.
        var propertyKind = Str(node["prop"]);
        var isStatic = k == "callStatic"
            || (k == "callInstance" && netType.IsAbstract && netType.IsSealed)
            || (k == "callInstance" && propertyKind is "get" or "set"
                && MemberIsStaticPropertyOrField(netType, method));
        var hasTypeArgs = node["typeArgs"] is JsonArray ta && ta.Count > 0;

        // A declaration loaded from a standard reference KLIB surfaces a CLR event as an ordinary read-only
        // `ClrEvent<T>` property. Unlike the retired FIR-injected declaration, it has no plugin origin from which kotc
        // could mint `clrEventGet`; recover that CLR shape here from the authoritative reference metadata.
        var projectedPropKind = Str(node["prop"]);
        var projectedEventName = method != null && method.StartsWith("get_", StringComparison.Ordinal)
            ? method[4..]
            : projectedPropKind == "get" ? method : null;
        if (projectedEventName != null &&
            _refs.TryClrEventIsStatic(bare, projectedEventName, out var eventIsStatic))
        {
            var recv = node["recv"];
            node.Clear();
            node["k"] = "clrEventGet";
            node["type"] = ownerJson?.DeepClone();
            node["name"] = projectedEventName;
            node["static"] = eventIsStatic;
            if (!eventIsStatic) node["recv"] = recv;
            return;
        }

        // Detach every current field (removing a key from a JsonObject detaches its value) so it can be re-added in the
        // CLR-shape order — byte-identical to what kotc used to emit directly, only the shape decision moved here.
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        // A declaration loaded from a regular Kotlin KLIB is not facadegen-injected, so kotc emits the ordinary
        // external-member dialect: the declared parameter types are in `sig`, not `argTypes`/`shapeTypes`.
        // Once the owner resolves against the authoritative CLR reference set, both fields carry the same frontend
        // fact needed here. Accepting `sig` lets bir2cir own the CLR binding without teaching kotc a KLIB side channel.
        JsonNode TakeMemberSig(string preferred) => Take(preferred) ?? Take("sig") ?? new JsonArray();
        JsonNode TakeDeclaredSig() =>
            Take("sig") ?? Take("shapeTypes") ?? Take("argTypes") ?? new JsonArray();
        var owner = Take("ownerType");
        var args = Take("args") as JsonArray ?? new JsonArray();
        // A `super.X()` (issue #14) rides in as `"super":true` on the callInstance. kotc already forced this call
        // non-virtual, but that intent is dropped when we reshape to a CLR node. Carry the flag onto the produced
        // clrInstance/clrPropGet/clrPropSet/clrGenericInstance so ilemit emits a non-virtual `call` (a base-slot
        // dispatch like C#'s `base.M()`) instead of a `callvirt` that would re-dispatch to THIS class's override.
        var superNode = Take("super");
        void CarrySuper() { if (superNode != null) node["super"] = superNode; }
        // Carry the frontend static-type stamp (#122) across the reshape (every key was detached above). Re-added once
        // here — the branches only ADD keys, never re-clear — so a LATE consumer (StringCharSequenceBridge) recovers the
        // reshaped node's Kotlin static type even when it is non-generic (no `ret`), e.g. a String-typed .NET property.
        if (Take("sty") is JsonNode styCarry) node["sty"] = styCarry;

        // GENERIC .NET method: the presence of `typeArgs` (a frontend fact) is the signal. ilemit MakeGenericMethods it.
        // W1-S1 (#46/#44): carry the FIR-RESOLVED member reference into CIR as `memberSig` — the callee's DECLARED
        // parameter types (kotc's pure-Kotlin `shapeTypes`), kept as STRUCTURED TypeNodes (OPEN: a method type-var stays
        // `{t:tv,scope:method}`). BirTypeLowering lowers each to the CLR vocabulary (added to TypeKeys), and ilemit does a
        // deterministic exact structural match (name + generic-arity + param-count + positional-tv equality), requiring
        // EXACTLY ONE candidate — no lossy `shapes` string, no first-pick. This replaces the retired ShapeSynthesis pass.
        if (hasTypeArgs)
        {
            node["k"] = isStatic ? "clrGenericStatic" : "clrGenericInstance";
            node["type"] = owner;
            node["method"] = method;
            node["typeArgs"] = Take("typeArgs");
            node["memberSig"] = NormalizeMemberSig(TakeMemberSig("shapeTypes") as JsonArray);
            if (!isStatic) node["recv"] = Take("recv");
            node["args"] = args;
            if (Take("suspendCall") is JsonNode sc1) node["suspendCall"] = sc1;
            if (!isStatic) CarrySuper();
            return;
        }

        // .NET ENUM consumed as a Kotlin Enum (#107): a facadegen-injected .NET enum carries a synthetic `kotlin.Enum<Self>`
        // supertype (facadegen `IsEnum` branch), so kotc resolves the INHERITED Kotlin Enum contract on a CONCRETE
        // .NET-enum receiver as a plain property-get by IDENTITY (`callInstance ownerType=System.DayOfWeek method=name/
        // ordinal prop=get`) — but System.Enum declares NEITHER as a .NET property (they'd fall through to a non-existent
        // `get_name`/`get_ordinal`). Bind them to the CLR enum semantics: `name` -> ToString() (the constant name, the
        // System.Enum override), `ordinal` -> the DECLARATION INDEX via `enumOrdinal` carrying the enum type (ilemit does
        // Array.IndexOf(Enum.GetValues(t), value) — Kotlin-faithful even for a sparse/negative/aliased .NET enum). The
        // GENERIC-receiver case (`e: T`, `T : Enum<T>`, owner kotlin.Enum) is handled separately by EnumMemberBinding.
        if (!isStatic && IsNetEnum(netType) && Str(v.TryGetValue("prop", out var pj) ? pj : null) == "get"
            && (method == "name" || method == "ordinal"))
        {
            var recv0 = Take("recv");
            if (method == "name") { node["k"] = "objMethod"; node["method"] = "ToString"; node["recv"] = recv0; }
            else { node["k"] = "enumOrdinal"; node["e"] = recv0; node["type"] = owner; }
            return;
        }

        // PROPERTY ACCESSOR by the frontend get/set KIND (A2 step 3): kotc emits the BARE property NAME + a
        // `"prop":"get"/"set"` marker (the accessor KIND — a frontend fact from correspondingPropertySymbol), NOT the
        // `get_`/`set_` .NET accessor slot. bir2cir APPLIES the .NET accessor convention off the refs: a real non-indexed
        // .NET property/field of that bare name -> clrPropGet/clrPropSet (the SAME node the legacy get_-prefix path
        // produces); otherwise (a synthetic member-extension / top-level-extension accessor with no matching .NET member)
        // reconstruct the `get_`/`set_<name>` plain method call and fall through — byte-identical to the old kotc emission.
        var propKind = Str(Take("prop"));
        // Standard KLIB metadata has an operator bit but no CLR-indexer side channel, so kotc emits plain operator
        // `get`/`set` calls. A real default indexed property and absence of a literal same-name method is sufficient
        // to recover the accessor without guessing its CLR name (custom IndexerName remains honored).
        if (propKind == null && method is "get" or "set" &&
            DefaultIndexerAccessor(netType, method == "set") is string projectedIndexerAccessor)
            method = projectedIndexerAccessor;
        // .NET DEFAULT INDEXED PROPERTY (A2 step 4): kotc emits the faithful Kotlin get/set operator identity
        // (`method:"get"/"set"`) + an index marker; it does NOT bake the `get_Item`/`set_Item` slot (WRONG for a custom
        // `[IndexerName]`). Resolve the .NET type's default indexed property off the refs (its DefaultMember/[IndexerName]
        // name) -> its `get_`/`set_` accessor method, then fall through to the PLAIN clrInstance method path — an indexer
        // is an INDEXED property, so MemberIsPropertyOrField excludes it and it stays a method call, byte-identical to the
        // old hardcoded `get_Item`/`set_Item` for the standard case.
        if (propKind == "index-get" || propKind == "index-set")
        {
            var isIxSet = propKind == "index-set";
            // A genuine .NET indexer -> its `get_`/`set_` accessor (a custom `[IndexerName]` honored via DefaultMember).
            // Otherwise (#133 case2): a facadegen-injected DotKt owner (a Kotlin-emitted type — e.g. `class Arr<T>` with
            // `operator fun get/set`) has NO .NET indexer property; it emitted the PLAIN operator method the frontend
            // named (`method` = "get"/"set", facadegen's clrName). Bind to that REAL method when the type declares it —
            // the get_Item/set_Item BCL fallback dangles on the Kotlin-emitted type. A DotKt owner declares the literal
            // `get`/`set`; a BCL indexer type declares `get_Item` (found by DefaultIndexerAccessor above), so this cleanly
            // separates the two. The get_Item/set_Item fallback survives for a BCL type whose indexer DefaultMember is
            // absent from the metadata (defensive; unchanged behavior).
            method = DefaultIndexerAccessor(netType, isIxSet)
                ?? (method != null && DeclaresPublicMethodNamed(netType, method) ? method : (isIxSet ? "set_Item" : "get_Item"));
        }
        else if (propKind == "get" || propKind == "set")
        {
            var isSet = propKind == "set";
            if (method != null && MemberIsPropertyOrField(netType, method))
            {
                if (!isSet)
                {
                    node["k"] = "clrPropGet";
                    node["type"] = owner;
                    node["name"] = method;
                    node["ret"] = Take("ret");
                    node["static"] = isStatic;
                    node["recv"] = isStatic ? null : Take("recv");
                    if (!isStatic) CarrySuper();
                    return;
                }
                node["k"] = "clrPropSet";
                node["type"] = owner;
                node["name"] = method;
                node["static"] = isStatic;
                node["recv"] = isStatic ? null : Take("recv");
                JsonNode setVal = null;
                if (args.Count > 0) { setVal = args[0]; args.RemoveAt(0); }
                node["value"] = setVal;
                if (!isStatic) CarrySuper();
                return;
            }
            // No matching .NET property/field -> a synthetic accessor METHOD: apply the get_/set_ convention and fall
            // through to the plain instance/static method path (byte-identical to the old kotc-baked get_/set_<name>).
            method = (isSet ? "set_" : "get_") + method;
        }

        // PROPERTY / FIELD accessor: a `get_X`/`set_X` that names a real .NET property (non-indexed) or field ->
        // clrPropGet/clrPropSet (ilemit emits the accessor call or an ldsfld/ldfld for a field-backed one). A `get_X`
        // that names NEITHER (a hand-written `get_`-prefixed method, an indexer `get_Item`, a synthetic
        // member-extension accessor) falls through to the plain method path below — exactly as kotc emitted before.
        if (method != null && (method.StartsWith("get_", StringComparison.Ordinal) || method.StartsWith("set_", StringComparison.Ordinal))
            && method.Length > 4 && MemberIsPropertyOrField(netType, method.Substring(4)))
        {
            var propName = method.Substring(4);
            if (method.StartsWith("get_", StringComparison.Ordinal))
            {
                node["k"] = "clrPropGet";
                node["type"] = owner;
                node["name"] = propName;
                node["ret"] = Take("ret");
                node["static"] = isStatic;
                node["recv"] = isStatic ? null : Take("recv");
                if (!isStatic) CarrySuper();
                return;
            }
            node["k"] = "clrPropSet";
            node["type"] = owner;
            node["name"] = propName;
            node["static"] = isStatic;
            node["recv"] = isStatic ? null : Take("recv");
            JsonNode value = null;
            if (args.Count > 0) { value = args[0]; args.RemoveAt(0); }   // detach args[0] from the (already-detached) array
            node["value"] = value;
            if (!isStatic) CarrySuper();
            return;
        }

        // .NET OPERATOR: kotc emits a .NET-type operator (`Vec2 + Vec2`, `-a`) as the PLAIN Kotlin operator identity
        // (`callInstance method="plus" recv:<a> args:[<b>]`) — it does NOT know the CLR `op_X` slot (layer purity).
        // Reconstruct the .NET static operator off the refs: map the Kotlin operator name to its `op_X` slot, confirm the
        // CLR type declares that `op_X` as a `public static` method (DON'T rewrite a Kotlin `plus` on a non-operator .NET
        // type), and emit `clrStatic op_X` with the receiver PREPENDED as the first arg (binary: [recv, arg]; unary
        // unaryMinus/unaryPlus/inc/dec: [recv] only). This is the exact node kotc used to emit directly (callStatic op_X,
        // receiver already prepended) -> byte-identical CIR. The receiver's type is the declaring .NET type = the owner,
        // mirroring kotc's old `birType(recv.type)` for argTypes[0].
        if (!isStatic && method != null && OperatorToNet.TryGetValue(method, out var opNet)
            && DeclaresPublicStaticMethod(netType, opNet))
        {
            var recv = Take("recv");
            var argTypes0 = TakeMemberSig("argTypes") as JsonArray ?? new JsonArray();
            var newArgTypes = new JsonArray { owner.DeepClone() };
            while (argTypes0.Count > 0) { var at = argTypes0[0]; argTypes0.RemoveAt(0); newArgTypes.Add(at); }
            var newArgs = new JsonArray { recv };
            while (args.Count > 0) { var a = args[0]; args.RemoveAt(0); newArgs.Add(a); }
            node["k"] = "clrStatic";
            node["type"] = owner;
            node["method"] = opNet;
            node["argTypes"] = newArgTypes;
            node["ret"] = Take("ret");
            node["args"] = newArgs;
            if (Take("suspendCall") is JsonNode scOp) node["suspendCall"] = scOp;
            return;
        }

        // #179 — a Kotlin `operator fun compareTo` on a facadegen-injected DotKt owner that implements Comparable<Self>.
        // The DotKt class's CLR slot is the PascalCase `System.IComparable<T>.CompareTo` (facadegen renamed the member +
        // restored the `kotlin.Comparable<Self>` supertype), but kotc emits the plain Kotlin name `compareTo` (a member
        // clrName is not a kotc channel), so a cross-module `c1 < c2` (the frontend resolves `<` to `compareTo`) would
        // dangle on a non-existent lowercase slot. The INSTANCE-slot analog of the plus->op_Addition rule above: rebind
        // `compareTo` -> `CompareTo` when the owner declares the `CompareTo` slot + a generic `IComparable` but NOT a
        // verbatim lowercase `compareTo` (a standalone non-Comparable `operator fun compareTo` keeps its own slot), then
        // fall through to the plain clrInstance path.
        if (!isStatic && method == "compareTo"
            && !DeclaresPublicMethodNamed(netType, "compareTo")
            && DeclaresPublicMethodNamed(netType, "CompareTo")
            && ImplementsGenericIComparable(netType))
            method = "CompareTo";

        // PLAIN static/instance method (incl. indexer get_Item/set_Item, member-extension synthetic accessor).
        node["k"] = isStatic ? "clrStatic" : "clrInstance";
        node["type"] = owner;
        node["method"] = method;
        // Resolve the CLR slot from the callee's complete declaration
        // signature. `args` may legitimately be shorter when Kotlin omits a
        // trailing optional parameter; ilemit backfills that value from the
        // resolved MethodInfo's DefaultParameterValue metadata.
        node["argTypes"] = TakeDeclaredSig();
        node["ret"] = Take("ret");
        if (!isStatic) node["recv"] = Take("recv");
        node["args"] = args;
        if (Take("suspendCall") is JsonNode sc2) node["suspendCall"] = sc2;
        if (!isStatic) CarrySuper();
    }

    // #73 M4-b — bind a `field`/`setField` on a facadegen-injected .NET owner to clrPropGet/clrPropSet. Resolves the
    // owner off the refs (skips kotlin.*/local owners); a name that is a real .NET property OR field (MemberIsProperty-
    // OrField matches both) is reshaped — EmitClrPropGet/Set falls through property -> get_ accessor -> field, so it
    // serves a genuine field too (with const-inlining + struct-safe receiver). A name the refs can't see stays plain.
    static void ReshapeField(JsonObject node, bool write)
    {
        var ownerJson = node["ownerType"];
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var netType = _refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name), ownerFqnNode.Args?.Length ?? 0);
        if (netType == null) return;
        var name = Str(node["name"]);
        if (name == null || !MemberIsPropertyOrField(netType, name)) return;
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        node["k"] = write ? "clrPropSet" : "clrPropGet";
        node["type"] = Take("ownerType");
        node["name"] = Take("name");
        node["static"] = false;
        node["recv"] = Take("recv");
        if (write) node["value"] = Take("value");
    }

    // #73 M4.4 — reshape a BOUND method-ref `newBoundDelegate` on a facadegen-injected .NET owner to the CLR
    // `newBoundClrDelegate` dialect node (ilemit resolves the target by reflection over the .NET type). Resolves the
    // owner off the refs (skips kotlin.*/local owners — those stay a plain newBoundDelegate ilemit binds via FindMethod).
    // The field set + order mirror kotc's former newBoundClrDelegate emission exactly (clrType from the owner identity,
    // method/argTypes/virtual/recv/funcType carried verbatim — including the method already Object-slot-renamed upstream).
    static void ReshapeBoundDelegate(JsonObject node)
    {
        // Only the .NET-bound producer (BirEmitter method-ref, clrOwner branch) carries `argTypes`; the Kotlin-owner
        // bound ref emits NONE. Gate on it so a cross-module Kotlin owner (a ProjectReference lib loaded via --ref,
        // which ResolveNetType WOULD resolve) is never mis-reshaped into a newBoundClrDelegate claiming `argTypes:[]`
        // — it stays the plain newBoundDelegate ilemit binds by FindMethod, exactly as before Wave 8.
        if (node["argTypes"] == null) return;
        var ownerJson = node["ownerType"];
        var ownerFqnNode = ownerJson == null ? null : UnwrapFqn(ownerJson);
        if (ownerFqnNode == null) return;
        var netType = _refs.ResolveNetType(ReferenceMetadataIndex.BareOwnerFqn(ownerFqnNode.Name), ownerFqnNode.Args?.Length ?? 0);
        if (netType == null) return;   // a Kotlin/local owner -> leave the plain newBoundDelegate for ilemit's handler
        var v = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var key in node.Select(kv => kv.Key).ToList()) { var val = node[key]; node.Remove(key); v[key] = val; }
        JsonNode Take(string key) => v.TryGetValue(key, out var x) ? x : null;
        node["k"] = "newBoundClrDelegate";
        node["clrType"] = Take("ownerType");
        node["method"] = Take("method");
        node["argTypes"] = Take("argTypes") ?? new JsonArray();
        node["virtual"] = Take("virtual");
        node["recv"] = Take("recv");
        node["funcType"] = Take("funcType");
    }

    // W1-S1 (#46): the clrGeneric* `memberSig` = the callee's declared param types, matched STRUCTURALLY by ilemit
    // against the reflected .NET method DEFINITION. Normalize each entry to how reflection presents the OPEN param:
    // a nullability ANNOTATION over a type-var (`T?`, `T!`) reflects as the bare open param `T` (there is no `T?` Type),
    // so unwrap `nullable`/`oblivious` around a `tv` at any depth. Without this the later NullableGenericErasure
    // pass object-erases a `nullable(tv)` entry (the boxed value rep) to `object`, which then fails to match the open
    // `T` param. A `nullable(value)` (`Int?` = `Nullable<Int32>`) is a real reflected type and is KEPT.
    static JsonNode NormalizeMemberSig(JsonArray shapeTypes)
    {
        var result = new JsonArray();
        if (shapeTypes != null)
            foreach (var st in shapeTypes)
                result.Add(TypeJson.Read(st) is TypeNode t ? TypeJson.Write(NormSigTv(t)) : st?.DeepClone());
        return result;
    }

    static TypeNode NormSigTv(TypeNode t) => t switch
    {
        TypeNode.Oblivious o => NormSigTv(o.Of),                          // annotation-only wrapper: always unwrap
        TypeNode.Nullable n => NormSigTv(n.Of) is TypeNode.Tv tv ? tv     // `T?` reflects as bare `T`
                               : new TypeNode.Nullable(NormSigTv(n.Of)),  // `Int?` stays Nullable<Int32>
        TypeNode.Fqn { Args: { } fa } f => new TypeNode.Fqn(f.Name, fa.Select(NormSigTv).ToArray()),
        TypeNode.Array a => new TypeNode.Array(NormSigTv(a.Elem)),
        TypeNode.ByRef b => new TypeNode.ByRef(NormSigTv(b.Of)),
        TypeNode.Fn fn => new TypeNode.Fn(fn.Suspend, NormSigTv(fn.Ret), fn.Params.Select(NormSigTv).ToArray(),
                                          fn.Recv == null ? null : NormSigTv(fn.Recv)),
        _ => t,
    };

    // Peel Nullable/Oblivious/ByRef wrappers off an owner type slot to reach the underlying .NET Fqn (name + type-args),
    // so a `List<Item>?`/`T!`/byref receiver resolves its open .NET definition. Also accepts a LEGACY STRING owner token
    // (kotc emits some owners — a referenced file class `LibKt`, the await marker `kotlin.clr.CoroutinesKt` — as a bare
    // string, not a structured `{t:fqn}` node); it carries no structured args (a method-generic's args live in
    // `typeArgs`). null when there is no Fqn underneath.
    static TypeNode.Fqn UnwrapFqn(JsonNode ownerJson)
    {
        if (ownerJson is JsonValue sv && sv.TryGetValue<string>(out var s) && s != null)
            return new TypeNode.Fqn(s);
        var t = TypeJson.Read(ownerJson);
        while (true)
            switch (t)
            {
                case TypeNode.Fqn f: return f;
                case TypeNode.Nullable nu: t = nu.Of; break;
                case TypeNode.Oblivious ob: t = ob.Of; break;
                case TypeNode.ByRef br: t = br.Of; break;
                default: return null;
            }
    }

    // The INVERSE of facadegen's OPERATOR_NAMES (facadegen Program.cs): a Kotlin `operator fun` name -> the .NET `op_X`
    // static-method slot. kotc emits the Kotlin identity; this pass reconstructs the .NET operator off the refs.
    static readonly Dictionary<string, string> OperatorToNet = new(StringComparer.Ordinal)
    {
        ["plus"] = "op_Addition", ["minus"] = "op_Subtraction", ["times"] = "op_Multiply", ["div"] = "op_Division",
        ["rem"] = "op_Modulus", ["unaryMinus"] = "op_UnaryNegation", ["unaryPlus"] = "op_UnaryPlus",
        ["inc"] = "op_Increment", ["dec"] = "op_Decrement",
    };

    // True iff the resolved owner is a .NET `enum` type (#107) — its members (name/ordinal) bind to the CLR enum
    // semantics rather than the plain property-accessor path. IsEnum is available on a MetadataLoadContext type.
    static bool IsNetEnum(Type type) { try { return type.IsEnum; } catch { return false; } }

    // True iff the .NET type declares `name` as a public static method (a `op_X` operator is a public static special
    // method on the declaring type). Guards against rewriting a Kotlin `plus` on a .NET type that has no such operator.
    static bool DeclaresPublicStaticMethod(Type type, string name)
    {
        try { return type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == name); }
        catch { return false; }
    }

    // The .NET DEFAULT INDEXED PROPERTY's `get_`/`set_` accessor slot name (A2 step 4). kotc's old hardcode was always
    // `get_Item`/`set_Item`; reflecting the type's `DefaultMemberAttribute` (which `[IndexerName("X")]` sets) honors a
    // custom-named indexer (e.g. `get_Chars`). Walks the type + bases + interfaces; prefers the indexed property whose
    // name matches the DefaultMember, else any indexed property. Returns the accessor MethodInfo.Name, or null if none.
    static string DefaultIndexerAccessor(Type type, bool isSet)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            string defaultMember = null;
            try
            {
                var dm = cur.GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.FullName == "System.Reflection.DefaultMemberAttribute");
                if (dm != null && dm.ConstructorArguments.Count > 0) defaultMember = dm.ConstructorArguments[0].Value as string;
            }
            catch { }
            try
            {
                PropertyInfo chosen = null;
                foreach (var p in cur.GetProperties(Flags))
                {
                    if (p.GetIndexParameters().Length == 0) continue;   // not an indexer
                    if (defaultMember != null && p.Name == defaultMember) { chosen = p; break; }
                    chosen ??= p;
                }
                if (chosen != null)
                {
                    var acc = isSet ? chosen.SetMethod : chosen.GetMethod;
                    if (acc != null) return acc.Name;
                }
            }
            catch { /* metadata-load edge on a malformed member table — keep walking */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return null;
    }

    // True iff the .NET type (or a base/interface) declares a NON-indexed property OR a field of this name — the two
    // members kotc's clrPropGet/clrPropSet covers (a property accessor, or a static/instance field read as ldsfld/ldfld).
    // An INDEXER (an indexed property, e.g. "Item") is excluded (it stays a plain get_Item/set_Item method call).
    internal static bool MemberIsPropertyOrField(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try
            {
                foreach (var p in cur.GetProperties(Flags))
                    if (p.Name == name && p.GetIndexParameters().Length == 0) return true;
                foreach (var fi in cur.GetFields(Flags))
                    if (fi.Name == name) return true;
            }
            catch { /* metadata-load edge on a malformed member table — treat as no match */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return false;
    }

    // The static/instance declaration bit for a property/field surfaced through Kotlin companion syntax. Refuse
    // ambiguous hierarchy collisions instead of choosing a first reflection result.
    static bool MemberIsStaticPropertyOrField(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var matches = new List<bool>();
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try
            {
                foreach (var p in cur.GetProperties(Flags))
                {
                    if (p.Name != name || p.GetIndexParameters().Length != 0) continue;
                    var accessor = p.GetMethod ?? p.SetMethod;
                    if (accessor != null) matches.Add(accessor.IsStatic);
                }
                foreach (var field in cur.GetFields(Flags))
                    if (field.Name == name) matches.Add(field.IsStatic);
            }
            catch { /* metadata-load edge — ambiguity/failure remains false */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return matches.Count == 1 && matches[0];
    }

    // True iff the .NET type (or a base/interface) declares a method of this name (any arity), public OR protected —
    // a Kotlin class can override a PROTECTED VIRTUAL .NET member (the WinUI OnLaunched pattern: `override fun Tag()`
    // over a protected `Base.Tag`). Used by DeclarationRename's facadegen-override slot resolution (A2 step 5) to
    // confirm a Kotlin override binds a REAL .NET method before it keeps the identity slot — facadegen injects the
    // Kotlin method identity EQUAL to the .NET name. NonPublic covers the protected/family case.
    internal static bool DeclaresPublicMethodNamed(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var seen = new HashSet<Type>();
        var stack = new Stack<Type>();
        stack.Push(type);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null || !seen.Add(cur)) continue;
            try { if (cur.GetMethods(Flags).Any(m => m.Name == name)) return true; }
            catch { /* metadata-load edge — keep walking */ }
            Type baseType = null; try { baseType = cur.BaseType; } catch { }
            if (baseType != null) stack.Push(baseType);
            try { foreach (var i in cur.GetInterfaces()) stack.Push(i); } catch { }
        }
        return false;
    }

    // True iff the .NET type implements the GENERIC `System.IComparable<T>` (its GetInterfaces set contains a
    // `System.IComparable`1` instantiation) — the marker that a DotKt owner's PascalCase `CompareTo` slot IS the
    // Comparable<Self> operator slot (#179), so a Kotlin `operator fun compareTo` call rebinds to it (rather than
    // dangling on the lowercased Kotlin name). The non-generic `System.IComparable` alone does NOT qualify.
    static bool ImplementsGenericIComparable(Type type)
    {
        try
        {
            return type.GetInterfaces().Any(i =>
                (i.IsGenericType ? i.GetGenericTypeDefinition().FullName : i.FullName) == "System.IComparable`1");
        }
        catch { return false; }
    }

    // ---- #178 Regex(options) ctor-arg conversion -----------------------------------------------

    const string RegexFqn = "kotlin.text.Regex";
    const string RegexOptionFqn = "kotlin.text.RegexOption";
    const string ClrRegexOptionsFqn = "kotlin.text.ClrRegexOptions";  // @ClrTypeAlias -> System...RegexOptions

    // The RegexOption ordinal -> System.Text.RegularExpressions.RegexOptions [Flags] bit, the exact INVERSE of
    // RegexClr.kt `nativeOptionsBits`. RegexOption's declaration order is IGNORE_CASE(0), MULTILINE(1), LITERAL(2),
    // UNIX_LINES(3), COMMENTS(4), DOT_MATCHES_ALL(5), CANON_EQ(6); only the four with a direct .NET bit appear — the
    // three .NET-unrepresentable options (LITERAL/UNIX_LINES/CANON_EQ) map to no bit (they encode to 0, the documented
    // CLR symmetry with the decode side, which never round-trips them either).
    static readonly (int Ordinal, int Bit)[] RegexOptionBits =
    {
        (0, 1),    // IGNORE_CASE      -> RegexOptions.IgnoreCase
        (1, 2),    // MULTILINE        -> RegexOptions.Multiline
        (5, 16),   // DOT_MATCHES_ALL  -> RegexOptions.Singleline
        (4, 32),   // COMMENTS         -> RegexOptions.IgnorePatternWhitespace
    };

    static int _rxCounter;

    // #178: `new kotlin.text.Regex(pattern, RegexOption)` / `new kotlin.text.Regex(pattern, Set<RegexOption>)` -> the
    // options arg is SYNTHESIZED into the OR'd RegexOptions bitmask (an Int) and its declared arg type is retyped to
    // `kotlin.text.ClrRegexOptions` (the @ClrTypeAlias twin of System...RegexOptions), so after BirTypeLowering the arg
    // is a `System...RegexOptions` Int that ClrMemberResolution matches EXACTLY against the BCL `Regex(String,
    // RegexOptions)` ctor. Runs before MemberCallSubstitution/BirTypeLowering (pure-Kotlin tokens) so the synthesized
    // `contains`/enum nodes lower through the ordinary passes. A no-op for any non-Regex `new` or non-option ctor.
    static void ReshapeRegexCtorOptions(JsonObject node)
    {
        if (TypeJson.Read(node["type"]) is not TypeNode.Fqn owner || owner.Name != RegexFqn) return;
        if (node["argTypes"] is not JsonArray argTypes || argTypes.Count != 2) return;
        if (node["args"] is not JsonArray args || args.Count != 2) return;
        var optType = TypeJson.Read(argTypes[1]);
        var single = optType is TypeNode.Fqn f1 && f1.Args == null && f1.Name == RegexOptionFqn;
        var set = optType is TypeNode.Fqn fs && fs.Name == "kotlin.collections.Set"
                  && fs.Args is { Length: 1 } sa && sa[0] is TypeNode.Fqn se && se.Name == RegexOptionFqn;
        if (!single && !set) return;

        var setType = argTypes[1];                 // the declared Set<RegexOption> type (needed before we retype it)
        var optArg = args[1];
        args.RemoveAt(1);                          // detach the option arg; the converted Int expr re-appends at index 1
        args.Add(single ? SingleOptionBits(optArg) : SetOptionBits(optArg, setType));
        argTypes[1] = TypeJson.Fqn(ClrRegexOptionsFqn);
    }

    // A single `RegexOption` value -> its RegexOptions bit: `d = ordinalOf(opt); d==0 ? 1 : d==1 ? 2 : d==5 ? 16 :
    // d==4 ? 32 : 0`. The ordinal is bound to a temp (valueBlock) so a side-effecting option expression runs exactly
    // once. `enumOrdinal` without a `type` lowers to Conv_I4 — RegexOption's underlying value == its ordinal (kotc emits
    // contiguous 0..n), so the bare underlying int IS the ordinal.
    static JsonNode SingleOptionBits(JsonNode optArg)
    {
        var name = "__rxopt$" + System.Threading.Interlocked.Increment(ref _rxCounter);
        var decl = new JsonObject
        {
            ["k"] = "var", ["name"] = name, ["type"] = TypeJson.Fqn("kotlin.Int"),
            ["init"] = new JsonObject { ["k"] = "enumOrdinal", ["e"] = optArg },
        };
        JsonNode chain = ConstInt(0);
        for (int i = RegexOptionBits.Length - 1; i >= 0; i--)
        {
            var (ord, bit) = RegexOptionBits[i];
            chain = new JsonObject
            {
                ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Int"),
                ["cond"] = new JsonObject
                {
                    ["k"] = "binOp", ["op"] = "==",
                    ["lhs"] = new JsonObject { ["k"] = "local", ["name"] = name }, ["rhs"] = ConstInt(ord),
                },
                ["then"] = ConstInt(bit), ["else"] = chain,
            };
        }
        return new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { decl }, ["result"] = chain };
    }

    // A `Set<RegexOption>` -> the OR'd RegexOptions bitmask: `(IGNORE_CASE in s ? 1:0) | (MULTILINE in s ? 2:0) |
    // (DOT_MATCHES_ALL in s ? 16:0) | (COMMENTS in s ? 32:0)`, with the set bound to a temp so it evaluates once. Each
    // `X in s` is the plain Kotlin `Set.contains(RegexOption)` call (MemberCallSubstitution binds it off the ref.dll,
    // exactly as a user `in`) with the option as a compile-time `enumValue` constant.
    static JsonNode SetOptionBits(JsonNode setArg, JsonNode setType)
    {
        var name = "__rxopts$" + System.Threading.Interlocked.Increment(ref _rxCounter);
        var decl = new JsonObject
        {
            ["k"] = "var", ["name"] = name, ["type"] = setType.DeepClone(), ["init"] = setArg,
        };
        JsonNode result = null;
        foreach (var (ord, bit) in RegexOptionBits)
        {
            var test = new JsonObject
            {
                ["k"] = "cond", ["type"] = TypeJson.Fqn("kotlin.Int"),
                ["cond"] = ContainsOption(name, ord, setType),
                ["then"] = ConstInt(bit), ["else"] = ConstInt(0),
            };
            result = result == null
                ? (JsonNode)test
                : new JsonObject { ["k"] = "binOp", ["op"] = "|", ["lhs"] = result, ["rhs"] = test };
        }
        return new JsonObject { ["k"] = "valueBlock", ["stmts"] = new JsonArray { decl }, ["result"] = result };
    }

    // `RegexOption.<entry at ordinal> in s` = `s.contains(enumValue)` — the exact node kotc emits for a Set `in` check
    // (virtual contains(T), the type-var sig, the Collection.contains override marker), so it binds identically.
    static JsonObject ContainsOption(string setLocal, int ordinal, JsonNode setType) => new()
    {
        ["k"] = "callInstance",
        ["ownerType"] = setType.DeepClone(),
        ["virtual"] = true,
        ["recv"] = new JsonObject { ["k"] = "local", ["name"] = setLocal },
        ["method"] = "contains",
        ["sig"] = new JsonArray { new JsonObject { ["t"] = "tv", ["scope"] = "type", ["i"] = 0 } },
        ["ret"] = TypeJson.Fqn("kotlin.Boolean"),
        ["args"] = new JsonArray { new JsonObject { ["k"] = "enumValue", ["type"] = TypeJson.Fqn(RegexOptionFqn), ["ordinal"] = ordinal } },
        ["overrides"] = new JsonArray
        {
            new JsonObject { ["owner"] = TypeJson.Fqn("kotlin.collections.Collection"), ["member"] = "contains", ["kind"] = "method", ["arity"] = 1 },
        },
    };

    static JsonObject ConstInt(int v) => new() { ["k"] = "const", ["type"] = TypeJson.Fqn("kotlin.Int"), ["value"] = v };
}
