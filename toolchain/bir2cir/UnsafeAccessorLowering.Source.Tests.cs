using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class UnsafeAccessorLowering
{
    static void RequireSourceAccess(bool value, string message)
    { if (!value) throw new InvalidOperationException("Typed lexical access: " + message); }
    static BirExpressionStatement SourceTestStatement(IBirExpression expression)
        => new() { Expr = OptionalField<IBirExpression>.Of(expression) };
    static IBirExpression SourceTestExpression(BirFunctionDeclaration method, int index)
        => ((BirExpressionStatement)method.Body.Value[index]).Expr.Value;
    sealed class SourceAccessTestTransitions : SuspendColdLowering.SourceDeclarationTransitions
    {
        internal readonly List<BirFunctionDeclaration> Methods = new();
        internal readonly List<IBirTypeDeclaration> Types = new();
        internal override BirGraphValue CloneIndependent(BirGraphValue source) => throw new InvalidOperationException("UnsafeAccess cloned an executable occurrence");
        internal override void RetireDiscardedSubtree(BirGraphValue source) => throw new InvalidOperationException("UnsafeAccess discarded an executable occurrence");
        internal override void RetireMethod(BirFileDeclaration file, IBirTypeDeclaration owner, BirFunctionDeclaration method) => throw new InvalidOperationException();
        internal override void ProjectMethod(BirFunctionDeclaration origin, BirFunctionDeclaration projection) => throw new InvalidOperationException();
        internal override void RetireTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> removed) => throw new InvalidOperationException();
        internal override void PublishMethods(BirFileDeclaration file, IBirTypeDeclaration owner, IReadOnlyList<BirFunctionDeclaration> methods)
        {
            foreach (var method in methods) RequireSourceAccess((owner?.Methods.OrEmpty() ?? file.Methods.OrEmpty())
                .Any(current => ReferenceEquals(current, method)), "method birth preceded attachment");
            Methods.AddRange(methods);
        }
        internal override void PublishTypes(BirFileDeclaration file, IReadOnlyList<IBirTypeDeclaration> types)
        {
            foreach (var type in types) RequireSourceAccess(file.Types.OrEmpty().Any(current => ReferenceEquals(current, type)), "holder birth preceded attachment");
            Types.AddRange(types);
        }
    }

    internal static void SourceSelfTest()
    {
        var integer = new TypeNode.Fqn("System.IO.MemoryStream");
        var ownerVariable = new TypeNode.Tv("type", 0);
        var methodVariable = new TypeNode.Tv("method", 0);
        var receiverType = new TypeNode.Fqn("Target", new TypeNode[] { integer });
        var receiverDeclaration = new BirLocalFunction { Id = "receiverLocal", Decl = new BirFunctionDeclaration {
            Name = "receiverLocal", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() } };
        var receiver = new BirValueBlock { Stmts = new IBirStatement[] { receiverDeclaration },
            Result = OptionalField<IBirExpression>.Of(new BirLocal { Name = "target", Sty = receiverType }), Sty = receiverType };
        var argumentDeclaration = new BirLocalFunction { Id = "argumentLocal", Decl = new BirFunctionDeclaration {
            Name = "argumentLocal", Params = Array.Empty<BirParameter>(), Ret = new TypeNode.Fqn("kotlin.Unit"), Body = Array.Empty<IBirStatement>() } };
        var argument = new BirValueBlock { Stmts = new IBirStatement[] { argumentDeclaration },
            Result = OptionalField<IBirExpression>.Of(new BirLocal { Name = "arg" }) };
        BirCallInstance Call(IBirExpression recv, IBirExpression arg) => new() {
            OwnerType = receiverType, Recv = OptionalField<IBirExpression>.Of(recv), Method = "secret", DeclarationId = "target-secret",
            Sig = new TypeNode[] { methodVariable }, TypeArgs = new TypeNode[] { integer }, Args = new[] { arg },
            MemberVisibility = "private", MemberOwnerTypeParams = new[] { new BirTypeParameter { Name = "T" } },
            MemberMethodTypeParams = new[] { new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { ownerVariable } } },
            MemberReturnType = ownerVariable, DynRet = integer, Sty = integer,
        };
        var first = Call(receiver, argument);
        var secondReceiver = new BirLocal { Name = "second", Sty = receiverType };
        var secondArgument = new BirLocal { Name = "secondArg" };
        var caller = new BirFunctionDeclaration { Name = "run", Params = new[] {
            SourceParameter("target", receiverType), SourceParameter("arg", integer),
            SourceParameter("second", receiverType), SourceParameter("secondArg", integer) }, Body = new IBirStatement[] {
            SourceTestStatement(first), SourceTestStatement(Call(secondReceiver, secondArgument)) } };
        var target = new BirFunctionDeclaration { Name = "secret", DeclarationId = "target-secret", Vis = "private",
            Params = new[] { SourceParameter("value", methodVariable) }, Ret = ownerVariable,
            NullableGenericRet = new TypeNode.Nullable(ownerVariable), TypeParams = new[] {
                new BirTypeParameter { Name = "M", Constraints = new TypeNode[] { ownerVariable }, SpecialConstraints = new[] { "class" } } } };
        var targetType = new BirClassDefinition { Name = "Target", TypeParams = new[] { new BirTypeParameter {
            Name = "T", Constraints = new TypeNode[] { new TypeNode.Fqn("System.IDisposable") } } }, Methods = new[] { target } };
        var root = new BirFileDeclaration { FileClass = "Caller", Methods = new[] { caller }, Types = new IBirTypeDeclaration[] { targetType } };
        var transitions = new SourceAccessTestTransitions();
        ApplyAll(new[] { root }, null, transitions);
        var rewritten = (BirCallStatic)SourceTestExpression(caller, 0);
        var repeated = (BirCallStatic)SourceTestExpression(caller, 1);
        var holder = (BirClassDefinition)transitions.Types.Single();
        var accessor = holder.Methods.Value[0];
        var wrapper = holder.Methods.Value[1];
        RequireSourceAccess(ReferenceEquals(rewritten.Args.Value[0], receiver) && ReferenceEquals(rewritten.Args.Value[1], argument)
            && ReferenceEquals(receiver.Stmts.Value[0], receiverDeclaration) && ReferenceEquals(argument.Stmts.Value[0], argumentDeclaration)
            && ReferenceEquals(repeated.Args.Value[0], secondReceiver) && ReferenceEquals(repeated.Args.Value[1], secondArgument),
            "receiver/arguments or registered descendants were copied");
        RequireSourceAccess(transitions.Methods.Count == 0 && root.Types.Value.Length == 2 && root.Methods.Value.Length == 1
            && repeated.Method.Value == rewritten.Method.Value && repeated.DeclarationId.Value == wrapper.DeclarationId.Value,
            "cache reuse, actual birth count, or holder ownership changed");
        RequireSourceAccess(accessor.TypeParams.Value[0].Constraints.Value[0] is TypeNode.Tv { Scope: "type", I: 0 }
            && holder.TypeParams.Value[0].Constraints.Value[0] is TypeNode.Fqn { Name: "System.IDisposable" }
            && accessor.UnsafeTargetDeclarationId.Value == "target-secret" && wrapper.UnsafeSignatureTargetDeclarationId.Value == "target-secret"
            && wrapper.NullableErasureOwnershipKnown.Value && !accessor.NullableErasureOwnershipKnown.IsPresent
            && wrapper.NullableGenericRet.Value is TypeNode.Nullable { Of: TypeNode.Tv { I: 0 } }
            && !ReferenceEquals(accessor.TypeParams.Value[0], target.TypeParams.Value[0]), "physical frames/nullable ownership were not retained independently");
        RequireSourceAccess(rewritten.Sty.Value is TypeNode.Fqn { Name: "System.IO.MemoryStream" }
            && !first.MemberVisibility.IsPresent && !first.MemberMethodTypeParams.IsPresent, "continuing call result or consumed facts changed");
        SourceFieldAndConstructorSelfTest();
        SourceConstructorScopeSelfTest();
        Console.WriteLine("[typed UnsafeAccessor] self-test OK (owning operands, exact frames, immediate births, cache and storage)");
    }

    static void SourceConstructorScopeSelfTest()
    {
        var tv = new TypeNode.Tv("type", 0);
        var derivedOwner = new TypeNode.Fqn("ConstructorDerived", new TypeNode[] { tv });
        var baseOwner = new TypeNode.Fqn("ConstructorBase", new TypeNode[] { tv });
        BirCallInstance Read() => new() { OwnerType = baseOwner, Recv = OptionalField<IBirExpression>.Of(SourceLocal("alias")),
            Method = "read", Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = tv };
        var baseRead = Read(); var thisRead = Read();
        BirConstructor Constructor(BirCallInstance call, bool delegatesBase)
        {
            var ctor = new BirConstructor { Params = new[] { SourceParameter("derived", derivedOwner) }, PreStmts = new IBirStatement[] {
                new BirVariable { Name = "alias", Type = derivedOwner, Init = OptionalField<IBirExpression>.Of(SourceLocal("derived")) } },
                Body = Array.Empty<IBirStatement>() };
            if (delegatesBase) ctor.BaseArgs = new IBirExpression[] { call };
            else ctor.ThisArgs = new IBirExpression[] { call };
            return ctor;
        }
        var first = Constructor(baseRead, true); var second = Constructor(thisRead, false);
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "ConstructorBase", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Methods = new[] { new BirFunctionDeclaration { Name = "read", Vis = "protected", Params = Array.Empty<BirParameter>(), Ret = tv } } },
            new BirClassDefinition { Name = "ConstructorDerived", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
                Base = baseOwner, Ctors = new[] { first, second } },
        } };
        var transitions = new SourceAccessTestTransitions();
        ApplyAll(new[] { root }, null, transitions);
        RequireSourceAccess(ReferenceEquals(first.BaseArgs.Value[0], baseRead) && ReferenceEquals(second.ThisArgs.Value[0], thisRead)
            && transitions.Methods.Count == 0 && transitions.Types.Count == 0, "constructor delegation lost preceding-local family scope");
    }

    static void SourceFieldAndConstructorSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var owner = new TypeNode.Fqn("Storage");
        var recv = new BirLocal { Name = "storage", Sty = owner };
        var assigned = new BirValueBlock { Stmts = Array.Empty<IBirStatement>(), Result = OptionalField<IBirExpression>.Of(new BirLocal { Name = "value" }) };
        var read = new BirFieldGet { OwnerType = owner, Name = "slot", Recv = OptionalField<IBirExpression>.Of(recv) };
        var writeRecv = new BirLocal { Name = "other", Sty = owner };
        var write = new BirSetField { OwnerType = owner, Name = "slot", Recv = OptionalField<IBirExpression>.Of(writeRecv), Value = OptionalField<IBirExpression>.Of(assigned) };
        var ctorArgument = new BirLocal { Name = "ctorArg" };
        var construction = new BirNew { Type = owner, ConstructorDeclarationId = "storage-ctor", MemberSignature = new TypeNode[] { integer }, Args = new IBirExpression[] { ctorArgument } };
        var caller = new BirFunctionDeclaration { Name = "run", Body = new IBirStatement[] { SourceTestStatement(read), write, SourceTestStatement(construction) } };
        var field = new BirFieldDeclaration { Name = "slot", Type = integer, Vis = "private", Volatile = true };
        var storage = new BirClassDefinition { Name = "Storage", Fields = new[] { field }, Ctors = new[] {
            new BirConstructor { DeclarationId = "storage-ctor", Vis = "private", Params = new[] { SourceParameter("value", integer) } } }, Methods = Array.Empty<BirFunctionDeclaration>() };
        var root = new BirFileDeclaration { FileClass = "StorageCaller", Methods = new[] { caller }, Types = new IBirTypeDeclaration[] { storage } };
        var transitions = new SourceAccessTestTransitions();
        ApplyAll(new[] { root }, null, transitions);
        var load = (BirByRefLoad)SourceTestExpression(caller, 0);
        var store = (BirByRefStore)SourceTestExpression(caller, 1);
        var made = (BirCallStatic)SourceTestExpression(caller, 2);
        RequireSourceAccess(load.Volatile.Value && store.Volatile.Value && ReferenceEquals(((BirCallStatic)load.Ptr.Value).Args.Value[0], recv)
            && ReferenceEquals(((BirCallStatic)store.Ptr.Value).Args.Value[0], writeRecv) && ReferenceEquals(store.Value.Value, assigned)
            && ReferenceEquals(made.Args.Value[0], ctorArgument), "volatile byref/constructor operands were not moved once");
        RequireSourceAccess(transitions.Methods.Count == 2 && transitions.Types.Count == 0
            && transitions.Methods[0].NullableErasureOwnershipKnown.Value
            && transitions.Methods[1].UnsafeSignatureTargetDeclarationId.Value == "storage-ctor"
            && !transitions.Methods[1].UnsafeTargetDeclarationId.IsPresent, "flat accessor identity or append count changed");
        var newClr = new BirNewClr { Type = owner, MemberSignature = OptionalField<TypeNode[]>.Null };
        var retained = new BirNew { Type = owner, MemberSignature = Array.Empty<TypeNode>(), MemberVisibility = "private" };
        caller.Body = new IBirStatement[] { SourceTestStatement(newClr), SourceTestStatement(retained) };
        DropFacts(new[] { root });
        RequireSourceAccess(newClr.MemberSignature.IsNull && retained.MemberSignature.HasValue && !retained.MemberVisibility.IsPresent,
            "reference-build fact dropping lost constructor signature presence");
    }

    internal static void SuperForwarderSelfTest()
    {
        var integer = new TypeNode.Fqn("kotlin.Int");
        var receiver = new BirLocal { Name = "outer", Sty = new TypeNode.Fqn("Child") };
        var call = new BirCallInstance { Super = true, DeclarationId = "base-read", OwnerType = new TypeNode.Fqn("Base"),
            Method = "read", Sig = Array.Empty<TypeNode>(), Args = Array.Empty<IBirExpression>(), Ret = integer,
            Recv = OptionalField<IBirExpression>.Of(receiver) };
        var invoke = new BirFunctionDeclaration { Name = "invoke", Body = new IBirStatement[] { new BirReturn { Value = OptionalField<IBirExpression>.Of(call) } } };
        var child = new BirClassDefinition { Name = "Child", Base = new TypeNode.Fqn("Base"), Methods = Array.Empty<BirFunctionDeclaration>() };
        var carrier = new BirClassDefinition { Name = "Carrier", NestedIn = "Child", Methods = new[] { invoke } };
        var root = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Base", Methods = new[] { new BirFunctionDeclaration { Name = "read", DeclarationId = "base-read", Params = Array.Empty<BirParameter>(), Ret = integer } } }, child, carrier } };
        var transitions = new SourceAccessTestTransitions();
        ApplyAll(new[] { root }, null, transitions);
        var forwarder = child.Methods.Value.Single();
        var replacement = (BirCallInstance)((BirReturn)invoke.Body.Value[0]).Value.Value;
        var baseCall = (BirCallInstance)((BirReturn)forwarder.Body.Value[0]).Value.Value;
        RequireSourceAccess(!replacement.DeclarationId.IsPresent && replacement.Method.Value == forwarder.Name.Value
            && ReferenceEquals(replacement.Recv.Value, receiver) && baseCall.DeclarationId.Value == "base-read" && baseCall.Recv.Value is BirThis
            && transitions.Methods.Single() == forwarder, "super forwarder confused physical and selected declaration identity");
        Console.WriteLine("[super forwarder] self-test OK (owning receiver, exact physical call, selected base identity)");
    }
}
