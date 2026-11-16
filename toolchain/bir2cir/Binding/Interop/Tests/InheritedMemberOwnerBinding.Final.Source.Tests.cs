using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class InheritedMemberOwnerBinding
{
    internal static void FinalSourceSelfTest()
    {
        static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Final inherited owner: " + message); }
        static BirExpressionStatement Expr(IBirExpression expression) => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
        static BirFunctionDeclaration Method(string name, params TypeNode[] signature) => new() {
            Name = name, Params = signature.Select((type, index) => new BirParameter { Name = "p" + index, Type = type }).ToArray(),
            Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>(),
        };
        static BirCallInstance Call(TypeNode owner, string method, params TypeNode[] signature) => new() {
            OwnerType = owner, Method = method, Sig = signature, Args = signature.Select((type, index) =>
                (IBirExpression)new BirLocal { Name = "p" + index, Sty = type }).ToArray(), Recv = new BirLocal { Name = "receiver" },
        };
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var text = new TypeNode.Fqn("System.String");
        var any = new TypeNode.Fqn("System.Object");
        var tv = new TypeNode.Tv("type", 0);
        var baseOpen = new TypeNode.Fqn("owner.Base", new TypeNode[] { tv });
        var baseText = new TypeNode.Fqn("owner.Base", new TypeNode[] { text });
        var derivedText = new TypeNode.Fqn("owner.Derived", new TypeNode[] { text });
        var baseMethod = Method("read", any); baseMethod.DeclarationId = "base-read"; baseMethod.Virtual = true; baseMethod.Ret = tv;
        var propertyMethod = Method("prop_get<slot>"); propertyMethod.PropertyName = "slot";
        propertyMethod.PropertyAccessor = "get"; propertyMethod.Virtual = true; propertyMethod.Ret = tv;
        var bridge = Method("prop_get<slot>"); bridge.PropertyName = "slot";
        bridge.PropertyAccessor = "get"; bridge.PhysicalSlotBridge = true;
        var methodTv = new TypeNode.Tv("method", 0);
        var genericMethod = Method("generic", methodTv); genericMethod.TypeParams = new[] { new BirTypeParameter { Name = "M" } };
        genericMethod.Ret = methodTv;
        var baseDefinition = new BirClassDefinition { Name = "owner.Base", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { baseMethod, propertyMethod, bridge, genericMethod } };
        var derivedDefinition = new BirClassDefinition { Name = "owner.Derived", Kind = "class", Base = baseOpen,
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = Array.Empty<BirFunctionDeclaration>() };
        var argument = new BirNewSuspendLambda { Params = Array.Empty<BirLambdaParameter>(), SuspendRet = any,
            Body = new IBirStatement[] { new BirReturn { Value = new BirConstant { Type = any, Value = new BirLiteral.NullLiteral() } } } };
        var receiver = new BirCallStatic { Method = "receiverOnce", Sty = derivedText, Args = Array.Empty<IBirExpression>() };
        var idCall = Call(derivedText, "read", any); idCall.DeclarationId = "base-read";
        idCall.Recv = receiver; idCall.Args = new IBirExpression[] { argument }; idCall.Ret = tv; idCall.Sty = any;
        var idArguments = idCall.Args.Value;
        var propertyCall = Call(derivedText, "slot"); propertyCall.Prop = "get"; propertyCall.Ret = tv; propertyCall.Virtual = true;
        var selected = Call(derivedText, "read", any); selected.ClrOwnerResolved = true;
        var native = new BirNativeInstance { Type = baseOpen, Recv = new BirLocal { Name = "n", Sty = derivedText },
            Method = "read", Ret = tv, Dispatch = "virtual" };
        var property = new BirPropertyGet { Type = baseOpen, Recv = new BirLocal { Name = "n", Sty = derivedText }, Member = "slot", Ret = tv };
        // Ret is NOT a receiver sty. It must not invent a declaration-frame application.
        var unstamped = new BirNativeInstance { Type = baseOpen, Recv = new BirCallStatic { Method = "unstamped", Ret = derivedText }, Ret = tv };
        var bareThis = Call(new TypeNode.Fqn("owner.Base"), "read", any); bareThis.Recv = new BirThis();
        var useMethod = Method("use"); useMethod.Body = new IBirStatement[] { Expr(bareThis) };
        derivedDefinition.Methods = new[] { useMethod };
        var genericCall = Call(derivedText, "generic", methodTv); genericCall.TypeArgs = new TypeNode[] { text }; genericCall.Ret = methodTv;
        var capturedTv = new TypeNode.Tv("type", 1);
        var innerCall = Call(new TypeNode.Fqn("owner.Base"), "read", any); innerCall.Recv = new BirThis(); innerCall.Ret = tv;
        var innerMethod = Method("use", any); innerMethod.Body = new IBirStatement[] { Expr(innerCall) };
        var inner = new BirClassDefinition { Name = "owner.Outer.Inner", Kind = "class", NestedIn = "owner.Outer",
            CapturedTypeParams = new[] { new BirTypeParameter { Name = "OuterT" } }, TypeParams = new[] { new BirTypeParameter { Name = "U" } },
            Base = new TypeNode.Fqn("owner.Base", new TypeNode[] { capturedTv }), Methods = new[] { innerMethod } };
        var payloadCall = Call(derivedText, "read", text); payloadCall.DeclarationId = "base-read";
        var payload = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, new BirInlinePayload {
            V = 1, Fqn = "owner.Consumer.hidden", Owner = "owner.Consumer", Static = true,
            Params = Array.Empty<BirParameter>(), Ret = any, Body = new IBirStatement[] { Expr(payloadCall) } });
        var payloadBytes = payload.ToArray();
        var use = Method("calls"); use.InlineBir = payload;
        use.Body = new IBirStatement[] { Expr(idCall), Expr(propertyCall), Expr(selected), Expr(native), Expr(property), Expr(unstamped), Expr(genericCall) };
        var file = new BirFileDeclaration { FileClass = "owner.Consumer", Methods = new[] { use },
            Types = new IBirTypeDeclaration[] { baseDefinition, derivedDefinition, inner } };
        var types = file.Types.Value; var methods = baseDefinition.Methods.Value; var body = use.Body.Value;
        ApplyAll(new[] { file }, refs);
        Require(idCall.OwnerType.Value == baseText && idCall.Sig.Value.SequenceEqual(new TypeNode[] { any })
            && idCall.Ret.Value == text && !idCall.Sty.IsPresent && idCall.Virtual.Value,
            "selected local ID did not bind current ABI/constructed result/dispatch");
        Require(propertyCall.OwnerType.Value == baseText && propertyCall.Virtual.Value && propertyCall.Ret.Value == text
            && propertyCall.Prop.Value == "get" && propertyCall.Method.Value == "slot"
            && KotlinPropertyAccessors.IsPhysicalSlotBridge(bridge),
            "property role or physical-bridge exclusion changed");
        Require(selected.OwnerType.Value == derivedText && !selected.Virtual.IsPresent
            && native.Type.Value == baseText && native.Ret.Value == text && native.Dispatch.Value == "virtual"
            && property.Type.Value == baseText && property.Ret.Value == text
            && unstamped.Type.Value == baseOpen && unstamped.Ret.Value == tv
            && bareThis.OwnerType.Value == baseOpen
            && innerCall.OwnerType.Value == new TypeNode.Fqn("owner.Base", new TypeNode[] { capturedTv }) && innerCall.Ret.Value == capturedTv
            && genericCall.OwnerType.Value == baseText && genericCall.Sig.Value.SequenceEqual(new TypeNode[] { methodTv })
            && genericCall.Ret.Value == methodTv && genericCall.TypeArgs.Value.SequenceEqual(new TypeNode[] { text }),
            "resolved owner, CLR construction, exact receiver stamp or enclosing complete frame changed");
        Require(ReferenceEquals(file.Types.Value, types) && ReferenceEquals(baseDefinition.Methods.Value, methods)
            && ReferenceEquals(use.Body.Value, body) && ReferenceEquals(idCall.Recv.Value, receiver)
            && ReferenceEquals(idCall.Args.Value, idArguments) && ReferenceEquals(idCall.Args.Value[0], argument)
            && use.InlineBir.Value.SequenceEqual(payloadBytes)
            && !ReferenceEquals(idCall.Sig.Value[0], baseMethod.Params.Value[0].Type.Value),
            "continuing declaration/operand ownership or opaque executable metadata changed");
        var nativeOwner = native.Type.Value;
        ApplyAll(new[] { file }, refs);
        Require(nativeOwner == native.Type.Value && idCall.Ret.Value == text,
            "already-closed physical result was substituted twice");

        // Last-wins type definitions and ambiguous-ID exclusion are independent
        // of hierarchy signature fitting; do not select a source twin by accident.
        var oldBase = new BirClassDefinition { Name = "owner.Base", Kind = "class",
            TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { Method("read", text) } };
        var old = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { oldBase } };
        idCall.OwnerType = derivedText; idCall.Sig = new TypeNode[] { any };
        ApplyAll(new[] { old, file }, refs);
        Require(idCall.Sig.Value.SequenceEqual(new TypeNode[] { any }) && idCall.OwnerType.Value == baseText,
            "current last-wins physical type lookup used an earlier declaration");
        var own = Method("read", any); own.Ret = tv; derivedDefinition.Methods = new[] { useMethod, own };
        var duplicate = Method("unrelated"); duplicate.DeclarationId = "base-read";
        var duplicateType = new BirClassDefinition { Name = "owner.Unrelated", Kind = "class", Methods = new[] { duplicate } };
        file.Types = types.Concat(new IBirTypeDeclaration[] { duplicateType }).ToArray();
        idCall.OwnerType = derivedText; idCall.Sig = new TypeNode[] { any };
        ApplyAll(new[] { file }, refs);
        Require(idCall.OwnerType.Value == derivedText && idCall.Sig.Value.SequenceEqual(new TypeNode[] { any }),
            "duplicate declaration identity overrode the current exact own slot");

        TestSuperAndOverrideClosure();
        TestInheritedDefault();
        Console.WriteLine("[final inherited owner] self-test OK (physical selection, IDs, virtual/super, default carrier, ownership, opaque metadata)");

        void TestSuperAndOverrideClosure()
        {
            var rootMethod = Method("run"); rootMethod.Virtual = true;
            var root = new BirClassDefinition { Name = "hierarchy.Root", Kind = "class", Methods = new[] { rootMethod } };
            var iface = new BirInterfaceDefinition { Name = "hierarchy.Contract", Kind = "interface", Methods = new[] { Method("run") } };
            var middle = new BirClassDefinition { Name = "hierarchy.Middle", Kind = "class", Base = new TypeNode.Fqn("hierarchy.Root"),
                Interfaces = new TypeNode[] { new TypeNode.Fqn("hierarchy.Contract") }, Methods = Array.Empty<BirFunctionDeclaration>() };
            var super = Call(new TypeNode.Fqn("hierarchy.Middle"), "run"); super.Super = true; super.Virtual = true;
            var interfaceSuper = Call(new TypeNode.Fqn("hierarchy.Contract"), "run"); interfaceSuper.Super = true;
            var obligation = Method("run"); obligation.Mods = new BirModifiers { Abstract = true };
            var abstractOwner = new BirClassDefinition { Name = "hierarchy.Abstract", Kind = "class", Base = new TypeNode.Fqn("hierarchy.Root"), Methods = new[] { obligation } };
            var ownCall = Call(new TypeNode.Fqn("hierarchy.Abstract"), "run");
            ownCall.Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("hierarchy.Root"), Member = "run" } };
            var far = new BirInterfaceDefinition { Name = "hierarchy.Far", Kind = "interface", Methods = new[] { Method("read") } };
            var near = new BirInterfaceDefinition { Name = "hierarchy.Near", Kind = "interface", Interfaces = new TypeNode[] { new TypeNode.Fqn("hierarchy.Far") }, Methods = new[] { Method("read") } };
            var receiverOwner = new BirClassDefinition { Name = "hierarchy.Leaf", Kind = "class", Interfaces = new TypeNode[] { new TypeNode.Fqn("hierarchy.Near") }, Methods = Array.Empty<BirFunctionDeclaration>() };
            var nearest = Call(new TypeNode.Fqn("hierarchy.Leaf"), "read");
            nearest.Overrides = new[] { new BirOverride { Owner = new TypeNode.Fqn("hierarchy.Far"), Member = "read" },
                new BirOverride { Owner = new TypeNode.Fqn("hierarchy.Near"), Member = "read" } };
            var other = new BirInterfaceDefinition { Name = "hierarchy.Other", Kind = "interface", Methods = new[] { Method("read") } };
            var diamond = new BirClassDefinition { Name = "hierarchy.Diamond", Kind = "class", Interfaces = new TypeNode[] {
                new TypeNode.Fqn("hierarchy.Near"), new TypeNode.Fqn("hierarchy.Other") }, Methods = Array.Empty<BirFunctionDeclaration>() };
            var ambiguous = Call(new TypeNode.Fqn("hierarchy.Diamond"), "read");
            var noSig = new BirCallInstance { OwnerType = new TypeNode.Fqn("hierarchy.Root"), Method = "run", Args = Array.Empty<IBirExpression>() };
            var external = Call(new TypeNode.Fqn("hierarchy.External"), "ToString"); external.Virtual = true;
            var externalOwner = new BirClassDefinition { Name = "hierarchy.External", Kind = "class", Base = any, Methods = Array.Empty<BirFunctionDeclaration>() };
            var consumer = Method("calls"); consumer.Body = new IBirStatement[] { Expr(super), Expr(interfaceSuper), Expr(ownCall), Expr(nearest), Expr(ambiguous), Expr(noSig), Expr(external) };
            var graph = new BirFileDeclaration { Methods = new[] { consumer }, Types = new IBirTypeDeclaration[] {
                root, iface, middle, abstractOwner, far, near, receiverOwner, other, diamond, externalOwner } };
            ApplyAll(new[] { graph }, refs);
            Require(super.OwnerType.Value == new TypeNode.Fqn("hierarchy.Root") && super.Virtual.HasValue && !super.Virtual.Value
                && interfaceSuper.OwnerType.Value == new TypeNode.Fqn("hierarchy.Contract")
                && !interfaceSuper.Virtual.IsPresent && ownCall.OwnerType.Value == new TypeNode.Fqn("hierarchy.Abstract")
                && nearest.OwnerType.Value == new TypeNode.Fqn("hierarchy.Near") && nearest.Virtual.Value
                && ambiguous.OwnerType.Value == new TypeNode.Fqn("hierarchy.Diamond") && !ambiguous.Virtual.IsPresent
                && noSig.Virtual.Value && !noSig.Sig.IsPresent && external.OwnerType.Value == any && external.Virtual.Value,
                "class-only super, own abstract obligation, nearest override closure, ambiguity or reference selection changed");
        }

        void TestInheritedDefault()
        {
            var signature = new TypeNode[] { tv };
            var externalOwner = new TypeNode.Fqn("defaults.External", signature);
            var fake = Method("map", tv); fake.FakeOverride = true;
            var external = new BirInterfaceDefinition { Name = "defaults.External", Kind = "interface",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Methods = new[] { Method("map", tv) } };
            var local = new BirInterfaceDefinition { Name = "defaults.Local", Kind = "interface",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Interfaces = new TypeNode[] { externalOwner }, Methods = new[] { fake },
                InheritedDefaultMethods = new[] { new BirInheritedDefaultMethod { Member = "map", Params = signature,
                    Implementation = new BirInheritedImplementation { Owner = externalOwner, Member = "defaultMap", TypeParams = Array.Empty<BirTypeParameter>() } } } };
            var recv = new BirLocal { Name = "value" };
            var bound = new BirNewBoundDelegate { OwnerType = new TypeNode.Fqn("defaults.Local", new TypeNode[] { text }),
                Recv = recv, Method = "map", Sig = new TypeNode[] { text }, Virtual = false };
            var consumer = Method("bound"); consumer.Body = new IBirStatement[] { Expr(bound) };
            var graph = new BirFileDeclaration { Methods = new[] { consumer }, Types = new IBirTypeDeclaration[] { external, local } };
            ApplyAll(new[] { graph }, refs);
            var expected = new TypeNode.Fqn("defaults.External", new TypeNode[] { text });
            Require(bound.OwnerType.Value == expected && bound.CalleeOwner.Value == expected
                && bound.Method.Value == "defaultMap" && bound.Virtual.Value && ReferenceEquals(bound.Recv.Value, recv)
                && !ReferenceEquals(((TypeNode.Fqn)bound.OwnerType.Value).Args, ((TypeNode.Fqn)bound.CalleeOwner.Value).Args),
                "inherited default carrier lost constructed owner, callee/dispatch or operand isolation");
        }
    }
}
