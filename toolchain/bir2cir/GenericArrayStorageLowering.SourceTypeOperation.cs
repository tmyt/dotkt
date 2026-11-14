using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class GenericArrayStorageLowering
{
    internal sealed record SourceTypeOperationFrame(IReadOnlyList<BirTypeParameter> Parameters,
        NullableRepresentationFrame Frame, IReadOnlyDictionary<int, TypeNode[]> SourceBounds);

    // The caller supplies the actual lexical declaration/frame facts. No Parent
    // link or reconstructed declaration is needed by the owning type operation.
    internal static BirGraphValue LowerTypeOperation(BirGraphValue operation, TypeNode target,
        SourceTypeOperationFrame owner, SourceTypeOperationFrame method, ReferenceMetadataIndex refs,
        Func<TypeNode, TypeNode> projectBound, Func<TypeNode, bool> isInterface,
        TypedGenericArrayStorageLowering.FreshNames names,
        Func<TypeNode, IBirExpression, IBirExpression> componentPredicate)
    {
        var nullable = target is TypeNode.Nullable;
        while (target is TypeNode.Nullable or TypeNode.Oblivious)
            target = target is TypeNode.Nullable n ? n.Of : ((TypeNode.Oblivious)target).Of;
        if (target is not TypeNode.Array array || !NeedsStorage(array)) return operation;
        var (kind, operand, reified) = operation switch {
            BirCast value => (value.Tag, value.E.OrDefault(), value.ReifiedTypeOperand.OrDefault()),
            BirInstanceOrNull value => (value.Tag, value.E.OrDefault(), value.ReifiedTypeOperand.OrDefault()),
            BirIsInstance value => (value.Tag, value.E.OrDefault(), false),
            _ => (null, (IBirExpression)null, false),
        };
        if (kind == null || KotlinTypeWitness.NeedsWitness(kind, reified) && ContainsVariable(array)) return operation;
        var test = kind == "isInst";
        var safe = test || kind == "isInstRef";
        var arrayName = names.Next("$arrayCast$");
        var typeName = names.Next("$arrayCastType$");
        var inputName = test && nullable ? names.Next("$arrayInput$") : null;
        var arrayType = new TypeNode.Fqn("System.Array");
        var typeType = new TypeNode.Fqn("System.Type");
        OptionalField<IBirExpression> E(IBirExpression value) => OptionalField<IBirExpression>.Of(value);
        BirConstant Boolean(bool value) => new() { Type = new TypeNode.Fqn("System.Boolean"), Value = new BirLiteral.BooleanLiteral(value) };
        BirConstant NullArray() => new() { Type = new TypeNode.Fqn("System.Array"), Value = new BirLiteral.NullLiteral() };
        BirLocal Read(string name, TypeNode type) => new() { Name = name, Sty = BirGraphValue.CopyType(type) };
        BirUnaryOperation Negate(IBirExpression value) => new() { Op = "!", E = E(value) };
        BirVariable Variable(string name, TypeNode type, IBirExpression value)
            => new() { Name = name, Type = BirGraphValue.CopyType(type), Init = E(value) };
        BirIf When(IBirExpression condition, params IBirStatement[] body)
            => new() { Branches = new[] { new BirBranch { Cond = E(condition), Body = body } } };
        IBirExpression CopyGenerated(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();
        SourceTypeOperationFrame Declaration(string scope) => scope == "method" ? method : owner;
        TypeNode PhysicalBound(TypeNode source) => source switch {
            TypeNode.Tv variable when Declaration(variable.Scope)?.Frame is { } frame =>
                frame.Variable(variable, NullableRepresentationFrame.Role.Ordinary),
            TypeNode.Nullable value => new TypeNode.Nullable(PhysicalBound(value.Of)),
            TypeNode.Oblivious value => new TypeNode.Oblivious(PhysicalBound(value.Of)),
            TypeNode.Array value => new TypeNode.Array(PhysicalBound(value.Elem), value.Rank, value.SzArray),
            TypeNode.Fqn value => new TypeNode.Fqn(value.Name, value.Args?.Select(PhysicalBound).ToArray()),
            _ => source,
        };
        TypeNode Bound(TypeNode.Tv variable)
        {
            var declaration = Declaration(variable.Scope);
            var frame = declaration?.Frame;
            var sourceIndex = frame?.PhysicalSlot(variable.I).SourceIndex ?? variable.I;
            var index = frame == null ? variable.I : frame.SourcePosition(frame.PhysicalSlot(variable.I).SourceIndex);
            if (declaration?.Parameters == null || index < 0 || index >= declaration.Parameters.Count)
                throw new InvalidOperationException("Array cast has no declaration-owned generic bound frame");
            var sourceBounds = declaration.SourceBounds?.GetValueOrDefault(sourceIndex);
            if (frame?.RequiresMetadata == true && sourceBounds == null)
                throw new InvalidOperationException("Array cast has no recorded Kotlin generic bounds");
            var parameter = declaration.Parameters[index];
            var bounds = sourceBounds ?? parameter.ErasedInnerConstraints.OrDefault() ?? parameter.Constraints.OrEmpty();
            var types = bounds.Where(type => type != null).ToArray();
            if (sourceBounds != null) types = types.Select(PhysicalBound).ToArray();
            return types.FirstOrDefault(type => !isInterface(type)) ?? types.FirstOrDefault() ?? new TypeNode.Fqn("System.Object");
        }
        IBirExpression ErasedComponent(TypeNode physical, IBirExpression actualType, IBirExpression classifier)
            => new BirConditional { Type = new TypeNode.Fqn("System.Boolean"),
                Cond = E(new BirObjectEquals { Lhs = E(CopyGenerated(actualType)),
                    Rhs = E(new BirClassReference { Type = BirGraphValue.CopyType(physical) }) }),
                Then = E(Boolean(true)), Else = E(classifier),
            };
        IBirExpression Check(TypeNode expected, IBirExpression actualType, HashSet<TypeNode.Tv> variables)
        {
            while (expected is TypeNode.Nullable or TypeNode.Oblivious or TypeNode.Projection)
                expected = expected switch {
                    TypeNode.Nullable value => value.Of, TypeNode.Oblivious value => value.Of,
                    TypeNode.Projection { Variance: "in" } => new TypeNode.Star(),
                    TypeNode.Projection value => value.Of, _ => expected,
                };
            if (expected is TypeNode.Star) return Boolean(true);
            if (expected is TypeNode.Tv variable)
            {
                if (!variables.Add(variable)) throw new InvalidOperationException("Cyclic array-cast erasure bound");
                return Check(Bound(variable), actualType, variables);
            }
            if (expected is TypeNode.Array nested)
            {
                var native = new BirConditional { Type = new TypeNode.Fqn("System.Boolean"),
                    Cond = E(SourceTypeOperationCall(refs, "System.Type", "get_IsSZArray", CopyGenerated(actualType), "System.Boolean", Array.Empty<string>())),
                    Then = E(Check(nested.Elem, SourceTypeOperationCall(refs, "System.Type", "GetElementType", actualType,
                        "System.Type", Array.Empty<string>()), new(variables))), Else = E(Boolean(false)),
                };
                return NeedsStorage(nested) ? ErasedComponent(new TypeNode.Fqn("System.Array"), actualType, native) : native;
            }
            if (expected is TypeNode.Fqn { Name: "kotlin.Any" or "System.Object" or "object", Args: null }) return Boolean(true);
            if (componentPredicate(expected, CopyGenerated(actualType)) is { } semantic)
                return ErasedComponent(projectBound(expected), actualType, semantic);
            if (expected is TypeNode.Fqn { Args: { Length: > 0 } } generic
                && ForeignStarProjectionBinding.TypeClassifierCheck(generic, CopyGenerated(actualType), refs) is { } foreign)
                return AliasVarianceRepresentation.RequiresErasure(generic, refs)
                    ? ErasedComponent(projectBound(generic), actualType, foreign) : foreign;
            return SourceTypeOperationCall(refs, "System.Type", "IsAssignableFrom",
                new BirClassReference { Type = BirGraphValue.CopyType(projectBound(expected)) },
                "System.Boolean", new[] { "System.Type" }, actualType);
        }
        var invalid = Check(array, Read(typeName, typeType), new());
        var exceptionOwner = refs.ResolveRefType("System.InvalidCastException");
        var exceptionConstructor = exceptionOwner.GetConstructor(System.Type.EmptyTypes);
        IBirStatement Failure() => safe
            ? new BirSetLocal { Name = arrayName, Value = E(NullArray()) }
            : new BirThrow { Value = E(new BirNewClr { Type = new TypeNode.Fqn("System.InvalidCastException"),
                Args = Array.Empty<IBirExpression>(), MemberRef = BirMemberReference.FromPhysical(
                    ClrMemberResolution.MemberRefOf(refs, exceptionConstructor, "ctor", exceptionOwner, null)) }) };
        BirBinaryOperation IsNull() => new() { Op = "===", Lhs = E(Read(arrayName, arrayType)), Rhs = E(NullArray()) };
        var statements = new List<IBirStatement>();
        if (inputName != null) statements.Add(Variable(inputName, new TypeNode.Fqn("System.Object"), operand));
        var input = inputName == null ? operand : Read(inputName, new TypeNode.Fqn("System.Object"));
        statements.Add(Variable(arrayName, arrayType, safe
            ? new BirInstanceOrNull { Type = BirGraphValue.CopyType(arrayType), E = E(input) }
            : new BirCast { Type = BirGraphValue.CopyType(arrayType), E = E(input) }));
        if (!safe && !nullable) statements.Add(When(IsNull(), Failure()));
        statements.Add(When(Negate(IsNull()),
            Variable(typeName, typeType, SourceTypeOperationCall(refs, "System.Object", "GetType", Read(arrayName, arrayType),
                "System.Type", Array.Empty<string>())), When(Negate(invalid), Failure())));
        IBirExpression result = !test ? Read(arrayName, arrayType) : inputName == null ? Negate(IsNull())
            : new BirConditional { Type = new TypeNode.Fqn("System.Boolean"),
                Cond = E(new BirBinaryOperation { Op = "==", Lhs = E(Read(inputName, new TypeNode.Fqn("System.Object"))), Rhs = E(NullArray()) }),
                Then = E(Boolean(true)), Else = E(Negate(IsNull())),
            };
        return new BirValueBlock { Type = new TypeNode.Fqn(test ? "System.Boolean" : "System.Array"),
            Stmts = statements.ToArray(), Result = E(result) };
    }

    static IBirExpression SourceTypeOperationCall(ReferenceMetadataIndex refs, string owner, string method,
        IBirExpression receiver, string result, string[] parameters, params IBirExpression[] arguments)
    {
        var reference = BirMemberReference.FromPhysical(
            ClrMemberResolution.ArrayStorageMethodOf(refs, method, parameters, owner, receiver == null));
        if (receiver == null) return new BirNativeStatic { Type = new TypeNode.Fqn(owner), Method = method,
            Recv = OptionalField<IBirExpression>.Null, Args = arguments,
            ArgTypes = parameters.Select(name => (TypeNode)new TypeNode.Fqn(name)).ToArray(), Ret = new TypeNode.Fqn(result),
            Sty = new TypeNode.Fqn(result), Dispatch = "call", MemberRef = reference };
        return new BirNativeInstance { Type = new TypeNode.Fqn(owner), Method = method,
            Recv = OptionalField<IBirExpression>.Of(receiver), Args = arguments,
            ArgTypes = parameters.Select(name => (TypeNode)new TypeNode.Fqn(name)).ToArray(), Ret = new TypeNode.Fqn(result),
            Sty = new TypeNode.Fqn(result), Dispatch = "callvirt", MemberRef = reference };
    }
}
