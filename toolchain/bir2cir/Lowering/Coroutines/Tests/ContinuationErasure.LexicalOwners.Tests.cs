using System;
using System.Collections.Generic;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ContinuationErasure
{
    static void LexicalOwnerSelfTest()
    {
        var names = new Dictionary<string, string> { [Cont] = "probe.ContinuationPhysical", [ResultFqn] = "probe.ResultPhysical" };
        foreach (var owner in new[] { Cont, ResultFqn })
        foreach (var selected in new[] { owner, names[owner] })
        {
            var tv = new TypeNode.Tv("type", 0);
            var exact = new TypeNode.Fqn(selected, new TypeNode[] { tv });
            var erased = new TypeNode.Fqn(selected, AnyArg);
            IBirExpression This() => new BirThis { Sty = exact };
            BirExpressionStatement Expr(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
            BirConstant Zero() => new() { Type = new TypeNode.Fqn("kotlin.Int"), Value = new BirLiteral.IntegerLiteral(0) };
            var method = new BirFunctionDeclaration { Name = "use", Ret = new TypeNode.Fqn("kotlin.Unit"),
                Params = new[] { new BirParameter { Name = "peer", Type = exact } }, Body = new IBirStatement[] {
                    Expr(new BirCallInstance { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()) }),
                    Expr(new BirFieldGet { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()) }),
                    new BirSetField { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()), Value = Zero() },
                    Expr(new BirFieldSetExpression { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()), Value = Zero() }),
                    Expr(new BirLateinitGet { OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()) }),
                    Expr(new BirCallInstance { OwnerType = exact, Recv = new BirLocal { Name = "peer", Sty = exact } }),
                    Expr(new BirStaticFieldGet { OwnerType = exact }),
                    Expr(new BirNew { Type = exact, Args = Array.Empty<IBirExpression>(), ArgTypes = Array.Empty<TypeNode>() }),
                } };
            var forward = new BirFunctionDeclaration { Name = "forward", Params = Array.Empty<BirParameter>(),
                Ret = new TypeNode.Fqn("kotlin.Unit"), Body = new IBirStatement[] { Expr(new BirCallInstance {
                    OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()), _exactExistentialBridgeOwnerCall = true,
                }) } };
            var sourceOwner = new BirClassDefinition { Kind = "class", Name = owner,
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Fields = Array.Empty<BirFieldDeclaration>(),
                Ctors = new[] { new BirConstructor { Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
                    new BirSetField { OwnerType = new TypeNode.Fqn(owner), Recv = new BirThis(), Value = Zero() },
                } } }, Methods = new[] { method, forward } };
            var sourceChild = new BirClassDefinition { Kind = "class", Name = "Child",
                TypeParams = new[] { new BirTypeParameter { Name = "T" } }, Interfaces = new TypeNode[] { exact },
                Methods = new[] { new BirFunctionDeclaration { Name = "inherited", Params = Array.Empty<BirParameter>(),
                    Ret = new TypeNode.Fqn("kotlin.Unit"), ClrInterfaceImpls = new[] {
                        new BirMethodImpl { Owner = exact, Arity = 0, Params = new TypeNode[] { exact }, Ret = exact },
                    }, Body = new IBirStatement[] { Expr(new BirCallInstance {
                        OwnerType = exact, Recv = OptionalField<IBirExpression>.Of(This()),
                    }) } } } };
            var source = new BirFileDeclaration { FileClass = "LexicalAbiProbe", Types = new IBirTypeDeclaration[] { sourceOwner, sourceChild } };
            GenericSelfInstantiation.ApplyAll(new[] { source });
            Apply(source, names);
            var body = method.Body.Value;
            BirGraphValue Access(int index) => body[index] is BirExpressionStatement statement ? (BirGraphValue)statement.Expr.Value : (BirGraphValue)body[index];
            TypeNode Owner(BirGraphValue access) {
                TypeNode result = null;
                access.VisitOwnTypeFields((key, type) => { if (key == "ownerType") result = type; });
                return result;
            }
            for (var index = 0; index < 5; index++)
            {
                var access = Access(index);
                EqualSourceType(Owner(access), exact, owner + " " + selected + " own member owner");
                BirGraphValue receiver = null;
                access.VisitOwnChildSlots((key, _, slot) => { if (key == "recv") receiver = slot.Value; });
                EqualSourceType(((BirThis)receiver).Sty.Value, exact, "implicit this stamp");
            }
            EqualSourceType(((BirSetField)sourceOwner.Ctors.Value[0].Body.Value[0]).OwnerType.Value,
                new TypeNode.Fqn(owner, new TypeNode[] { tv }), "constructor field owner");
            EqualSourceType(((BirCallInstance)((BirExpressionStatement)forward.Body.Value[0]).Expr.Value).OwnerType.Value, exact, "exact forwarding target");
            EqualSourceType(((BirCallInstance)Access(5)).OwnerType.Value, erased, "ordinary peer owner");
            EqualSourceType(((BirLocal)((BirCallInstance)Access(5)).Recv.Value).Sty.Value, erased, "ordinary peer value");
            EqualSourceType(((BirStaticFieldGet)Access(6)).OwnerType.Value, erased, "static owner");
            EqualSourceType(((BirNew)Access(7)).Type.Value, erased, "allocation ABI");
            EqualSourceType(method.Params.Value[0].Type.Value, erased, "parameter ABI");
            EqualSourceType(method.Params.Value[0].KotlinType.Value, exact, "parameter source surface");
            EqualSourceType(sourceChild.Interfaces.Value[0], erased, "implemented ancestor ABI");
            var childMethod = sourceChild.Methods.Value[0];
            EqualSourceType(((BirCallInstance)((BirExpressionStatement)childMethod.Body.Value[0]).Expr.Value).OwnerType.Value, erased, "inherited this target");
            var descriptor = childMethod.ClrInterfaceImpls.Value[0];
            EqualSourceType(descriptor.Owner.Value, erased, "MethodImpl owner");
            EqualSourceType(descriptor.Params.Value[0], erased, "MethodImpl parameter");
            EqualSourceType(descriptor.Ret.Value, erased, "MethodImpl return");
            if (ReferenceEquals(((TypeNode.Fqn)method.Params.Value[0].Type.Value).Args, ((TypeNode.Fqn)((BirNew)Access(7)).Type.Value).Args))
                throw new InvalidOperationException("Continuation erasure shared mutable physical argument vectors");
        }
        ExactEnclosingFrames(names);
        ExactOuterModelSelfTest();
        Console.WriteLine("[coroutine lexical owners] self-test OK (own frames, enclosing storage, values and ancestor ABI)");
    }

    static void ExactEnclosingFrames(IReadOnlyDictionary<string, string> names)
    {
        var captured = new TypeNode.Fqn(ResultFqn, new TypeNode[] { new TypeNode.Tv("type", 1) });
        var nested = new TypeNode.Fqn("probe.Frame", new TypeNode[] { captured });
        var receiver = new BirFieldGet { Name = "receiver", Outer = true, Ret = nested, Sty = nested, _nativeFieldAddress = true };
        var call = new BirCallInstance { OwnerType = nested, Recv = OptionalField<IBirExpression>.Of(receiver) };
        var ordinary = new BirVariable { Name = "ordinary", Type = nested };
        var alias = new BirVariable { Name = "exactAlias", Type = nested, Outer = true };
        var field = new BirFieldDeclaration { Name = "receiver", Type = nested, Outer = true, _nativeLocation = true };
        var parameter = new BirParameter { Name = "receiver", Type = nested, Outer = true };
        var method = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(),
            Body = new IBirStatement[] { new BirExpressionStatement { Expr = call }, ordinary, alias } };
        var owner = new BirClassDefinition { Kind = "class", Name = "Capture",
            TypeParams = new[] { new BirTypeParameter { Name = "Outer" }, new BirTypeParameter { Name = "Actual" } },
            Fields = new[] { field }, Ctors = new[] { new BirConstructor { Params = new[] { parameter } } }, Methods = new[] { method } };
        var file = new BirFileDeclaration { Types = new IBirTypeDeclaration[] { owner } };
        Apply(file, names);
        foreach (var exact in new[] { field.Type.Value, parameter.Type.Value, call.OwnerType.Value, receiver.Ret.Value, receiver.Sty.Value, alias.Type.Value })
            EqualSourceType(exact, nested, "exact captured frame");
        EqualSourceType(ordinary.Type.Value, new TypeNode.Fqn("probe.Frame", new TypeNode[] { new TypeNode.Fqn(ResultFqn, AnyArg) }), "ordinary captured value");
        if (field.KotlinType.IsPresent || parameter.KotlinType.IsPresent || !ReferenceEquals(call.Recv.Value, receiver))
            throw new InvalidOperationException("Exact enclosing storage changed source metadata or operand ownership");
        ConsumeExactOuterSource(file);
        if (receiver.Outer.IsPresent || field.Outer.IsPresent || parameter.Outer.IsPresent || alias.Outer.IsPresent
            || !field._nativeLocation.Value || !receiver._nativeFieldAddress.Value)
            throw new InvalidOperationException("Exact outer consumption retained its role or erased a required physical-location proof");
    }

    static void ExactOuterModelSelfTest()
    {
        static void Need(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Exact outer model coverage: " + message); }
        var integer = new TypeNode.Fqn("kotlin.Int");
        BirConstant Zero() => new() { Type = integer, Value = new BirLiteral.IntegerLiteral(0) };
        BirExpressionStatement Expr(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        var read = new BirFieldGet { Name = "$outer", Ret = integer, _nativeFieldAddress = true };
        var writeExpression = new BirFieldSetExpression { Name = "$outer", Value = Zero() };
        var write = new BirSetField { Name = "$outer", Value = Zero() };
        var local = new BirLocal { Name = "$outer", Sty = integer };
        var self = new BirThis { Sty = integer };
        var variable = new BirVariable { Name = "$outer", Type = integer, Init = Zero() };
        var field = new BirFieldDeclaration { Name = "$outer", Type = integer, _nativeLocation = true };
        var parameter = new BirParameter { Name = "$outer", Type = integer };
        var lambdaParameter = new BirLambdaParameter { Name = "$outer", Type = integer };
        var capture = new BirCaptureDescriptor { Name = "$outer", Type = integer };
        var inlineCapture = new BirInlineCapture { Name = "$outer", Type = integer, Value = Zero() };
        BirGraphValue[] roles = { read, writeExpression, write, local, self, variable, field, parameter,
            lambdaParameter, capture, inlineCapture };
        // Reflection is test-only: a new modeled carrier must extend both the
        // actual role reader and the consumer, not silently default through them.
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        var modeled = typeof(BirGraphValue).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(BirGraphValue).IsAssignableFrom(type)
                && type.GetProperty("Outer", flags) is not null).ToHashSet();
        Need(modeled.SetEquals(roles.Select(role => role.GetType())) && roles.Length == 11,
            "the generated Outer-bearing records and explicit coverage differ");
        var properties = roles.Select(role => role.GetType().GetProperty("Outer", flags)!).ToArray();
        Need(properties.All(property => property.PropertyType == typeof(OptionalField<bool>)), "Outer fact type changed");

        var inline = new BirInlineLambda { Params = new[] { lambdaParameter }, Captures = new[] { inlineCapture },
            Body = Array.Empty<IBirStatement>(), Result = Zero() };
        var suspend = new BirNewSuspendLambda { Arity = 0, Params = Array.Empty<BirLambdaParameter>(),
            Captures = new[] { capture }, CapValues = new IBirExpression[] { Zero() }, SuspendRet = integer,
            Body = Array.Empty<IBirStatement>() };
        var frozen = ExecutableBirCodec.EncodeInline(ExecutableBirCodec.Codec, new BirInlinePayload {
            V = 1, Fqn = "OuterRoles.inline", Owner = "OuterRoles", Recv = "none", Static = true,
            Params = new[] { new BirParameter { Name = "$outer", Type = integer, Outer = true } }, Ret = integer,
            Body = new IBirStatement[] { new BirReturn { Value = Zero() } },
        });
        var frozenBytes = frozen.ToArray();
        var body = new IBirStatement[] { Expr(read), Expr(writeExpression), write, Expr(local), Expr(self), variable,
            Expr(inline), Expr(suspend) };
        var method = new BirFunctionDeclaration { Name = "use", Params = new[] { parameter }, Body = body, InlineBir = frozen };
        var file = new BirFileDeclaration { Methods = new[] { method }, Fields = new[] { field } };
        var states = new[] { default(OptionalField<bool>), OptionalField<bool>.Null,
            OptionalField<bool>.Of(false), OptionalField<bool>.Of(true) };
        foreach (var state in states)
        {
            for (var index = 0; index < roles.Length; index++)
            {
                properties[index].SetValue(roles[index], state);
                Need(SourceExactOuter(roles[index]) == state.OrDefault(), "role reader disagrees on " + roles[index].GetType().Name);
            }
            ConsumeExactOuterSource(file);
            for (var index = 0; index < roles.Length; index++)
                Need(!((OptionalField<bool>)properties[index].GetValue(roles[index])!).IsPresent
                    && !SourceExactOuter(roles[index]), "consumer missed " + roles[index].GetType().Name);
            Need(ReferenceEquals(method.Body.Value, body) && ReferenceEquals(file.Fields.Value[0], field)
                && ReferenceEquals(inline.Params.Value[0], lambdaParameter) && ReferenceEquals(inline.Captures.Value[0], inlineCapture)
                && ReferenceEquals(suspend.Captures.Value[0], capture) && field._nativeLocation.Value && read._nativeFieldAddress.Value,
                "consumption replaced actual records/vectors or erased a physical-location proof");
            Need(ReferenceEquals(method.InlineBir.Value, frozen) && frozen.AsSpan().SequenceEqual(frozenBytes)
                && ExecutableBirCodec.ReadInline(ExecutableBirCodec.Codec, frozen).Params.Value[0].Outer.Value,
                "consumption rewrote frozen executable metadata");
        }
    }
}
