using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;
using static PhysicalValueCoercion;

// Final physical value edges over owning occurrences. Earlier DOM owners still
// share the scalar conversion policy; this path never materializes a DOM graph.
static class TypedPhysicalValueCoercion
{
    static readonly TypeNode Void = new TypeNode.Fqn("void");
    static readonly TypeNode Object = new TypeNode.Fqn("System.Object");
    static bool IsVoid(TypeNode type) => type is TypeNode.Fqn { Args: null, Name: "void" or "System.Void" };
    static BirGraphValue Graph(IBirExpression expression) => expression as BirGraphValue;
    static IBirExpression Expression(BirGraphValue value) => (IBirExpression)value;
    static OptionalField<IBirExpression> Field(IBirExpression expression) => OptionalField<IBirExpression>.Of(expression);
    static TypeNode[] OwnerArgs(BirMemberReference member) => (member?.DeclaringType.OrDefault() as TypeNode.Fqn)?.Args ?? [];

    internal static void ApplyAll(IReadOnlySet<string> localTypes, PhysicalDelegateDeclarations declarations, IReadOnlyDictionary<string, string> localCarriers,
        IReadOnlyList<BirFileDeclaration> roots, Func<IBirExpression> unitValue,
        ValueTypeOracle isValue, bool referenceBuild = false, Func<TypeNode.Fqn, bool> isReferenceOwner = null,
        Func<TypeNode.Fqn, bool> isExistentialOwner = null, Func<TypeNode.Fqn, bool> isNativeReference = null,
        Func<TypeNode.Fqn, TypeNode.Fqn, bool> referencedCarrier = null, ReferenceMetadataIndex references = null)
    {
        var index = new Index(roots, unitValue) {
            IsValue = isValue, ReferenceBuild = referenceBuild,
            IsReferenceOwner = isReferenceOwner ?? (_ => false),
            IsExistentialOwner = isExistentialOwner ?? (_ => false),
            IsNativeReference = isNativeReference ?? (_ => false),
            ReferencedCarrier = referencedCarrier, References = references, LocalCarriers = localCarriers,
            DelegateAdapters = new TypedPhysicalDelegateAdapter(localTypes, references, unitValue, declarations),
            LocalFunctionRuntime = roots.Any(root => root.FileClass.OrDefault() == FunctionValueRepresentation.FunctionRuntime),
            LocalArrayRuntime = roots.Any(root => root.FileClass.OrDefault() == ArrayRuntime),
        };
        if (localCarriers != null)
            foreach (var (owner, carrier) in localCarriers) index.CarrierClasses[carrier] = owner;
        foreach (var root in roots)
        {
            index.Document = root;
            index.ArrayNames = new BirTemporaryNames(root);
            RewriteMembers(root.Fields.OrEmpty(), [], root.Methods.OrEmpty(),
                root.FileClass.HasValue ? new TypeNode.Fqn(root.FileClass.Value) : null, index);
            // Adapters appended by a constructor also have a native result
            // boundary. Visit each appended declaration once, in creation order.
            for (var i = 0; i < root.Types.OrEmpty().Length; i++) RewriteType(root.Types.Value[i], index);
        }
    }

    internal static Func<BirGraphValue, IReadOnlyDictionary<string, TypeNode>, TypeNode> ResultTypeOracle(
        IReadOnlyList<BirFileDeclaration> roots)
    {
        var index = new Index(roots, () => throw new InvalidOperationException("Type lookup cannot materialize Unit"));
        var fields = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        return (node, locals) => {
            if (node is BirNewSam { SamType.HasValue: true } construction
                && construction.SamType.Value is TypeNode.Fqn implementation)
                return construction.TypeArgs.OrEmpty() is { Length: > 0 } args
                    ? new TypeNode.Fqn(implementation.Name, args) : implementation;
            return ConstructedReferenceType(node, fields, locals)
                ?? ExprType(node, new Scope(locals: locals as Dictionary<string, TypeNode>
                    ?? new(locals, StringComparer.Ordinal)), index);
        };
    }

    sealed record MethodShape(string Owner, string Name, int Arity, TypeNode[] Parameters, TypeNode Return);
    readonly record struct TypeShape(string Name,
        BirFieldDeclaration[] Fields, BirConstructor[] Constructors, BirFunctionDeclaration[] Methods);

    static TypeShape Shape(IBirTypeDeclaration declaration) => declaration switch {
        BirClassDefinition type => new(type.Name.OrDefault(), type.Fields.OrEmpty(), type.Ctors.OrEmpty(), type.Methods.OrEmpty()),
        BirInterfaceDefinition type => new(type.Name.OrDefault(), type.Fields.OrEmpty(), type.Ctors.OrEmpty(), type.Methods.OrEmpty()),
        BirEnumDefinition type => new(type.Name.OrDefault(), type.Fields.OrEmpty(), type.Ctors.OrEmpty(), type.Methods.OrEmpty()),
        _ => throw new InvalidOperationException("Unaccounted physical declaration " + declaration.GetType().Name),
    };

    sealed class Index
    {
        readonly Dictionary<string, List<MethodShape>> methods = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeNode> fields = new(StringComparer.Ordinal);
        readonly Dictionary<string, TypeNode[]> closureConstructors = new(StringComparer.Ordinal);
        readonly HashSet<string> nonGenericClasses = new(StringComparer.Ordinal);
        readonly HashSet<(string Class, string Carrier)> declaredCarriers = new();
        readonly Dictionary<(TypeNode Source, TypeNode Target), bool> referenceNarrowing = new();
        readonly Dictionary<string, SupertypeGraph.Def> types;
        internal readonly Func<IBirExpression> UnitValue;
        internal ValueTypeOracle IsValue = _ => false;
        internal Func<TypeNode.Fqn, bool> IsReferenceOwner = _ => false;
        internal Func<TypeNode.Fqn, bool> IsExistentialOwner = _ => false;
        internal Func<TypeNode.Fqn, bool> IsNativeReference = _ => false;
        internal Func<TypeNode.Fqn, TypeNode.Fqn, bool> ReferencedCarrier;
        internal bool ReferenceBuild;
        internal bool LocalFunctionRuntime;
        internal bool LocalArrayRuntime;
        internal ReferenceMetadataIndex References;
        BirMemberReference isValueType;
        internal BirNativeInstance IsValueType(TypeNode variable) =>
            PhysicalTypeKindTest.IsValueType(variable, isValueType ??= PhysicalTypeKindTest.Bind(References));
        BirMemberReference asReference;
        internal BirGenericStatic ReferenceView(TypeNode from, TypeNode to, IBirExpression address)
        {
            const string owner = "System.Runtime.CompilerServices.Unsafe";
            if (asReference == null)
            {
                TypeNode[] signature = [new TypeNode.ByRef(new TypeNode.Tv("method", 0))];
                if (!References.TryResolveStaticMemberSignature(owner, "As", 2, true, signature, null,
                        out _, out var method, out var declaring))
                    throw new InvalidOperationException("Missing CLR managed-reference identity conversion");
                asReference = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, References, method, "method", declaring, null));
            }
            return new() { Type = new TypeNode.Fqn(owner), Method = "As", TypeArgs = new[] { from, to },
                MemberRef = asReference, Args = new[] { address } };
        }
        internal IReadOnlyDictionary<string, string> LocalCarriers;
        internal BirFileDeclaration Document;
        internal BirTemporaryNames ArrayNames;
        internal TypedPhysicalDelegateAdapter DelegateAdapters;

        internal Index(IReadOnlyList<BirFileDeclaration> roots, Func<IBirExpression> unitValue)
        {
            UnitValue = unitValue;
            types = SupertypeGraph.CollectSource(roots);
            foreach (var root in roots)
            {
                AddMembers(root.FileClass.OrDefault(), root.Fields.OrEmpty(), root.Methods.OrEmpty());
                foreach (var declaration in root.Types.OrEmpty())
                {
                    var type = Shape(declaration);
                    if (type.Name == null) continue;
                    var hierarchy = types[type.Name];
                    if (type.Constructors.Length == 1 && type.Constructors[0].Params.HasValue)
                        closureConstructors[type.Name] = type.Constructors[0].Params.Value.Select(p => p.Type.OrDefault()).ToArray();
                    if (hierarchy.Kind == "class" && hierarchy.Arity == 0) nonGenericClasses.Add(type.Name);
                    if (hierarchy.Kind is "class" or "interface")
                        foreach (var implemented in hierarchy.Interfaces.Where(t => t.Args == null))
                            declaredCarriers.Add((type.Name, implemented.Name));
                    AddMembers(type.Name, type.Fields, type.Methods);
                }
            }
        }

        void AddMembers(string owner, BirFieldDeclaration[] declarations, BirFunctionDeclaration[] functions)
        {
            if (owner == null) return;
            foreach (var field in declarations)
                if (field.Name.HasValue && field.Type.HasValue) fields[Key(owner, field.Name.Value)] = field.Type.Value;
            foreach (var method in functions)
            {
                if (!method.Name.HasValue || !method.Ret.HasValue || !method.Params.HasValue) continue;
                var parameters = method.Params.Value.Select(p => p.Type.OrDefault()).ToArray();
                if (parameters.Any(p => p == null)) continue;
                var shape = new MethodShape(owner, method.Name.Value, method.TypeParams.OrEmpty().Length, parameters, method.Ret.Value);
                var key = Key(owner, method.Name.Value);
                if (!methods.TryGetValue(key, out var bucket)) methods[key] = bucket = new();
                bucket.Add(shape);
            }
        }

        static string Key(string owner, string name) => owner + "\0" + name;
        internal TypeNode Field(string owner, string name) => owner != null && name != null
            && fields.TryGetValue(Key(owner, name), out var type) ? type : null;
        internal bool ExactReferenceOwner(TypeNode.Fqn owner) => IsReferenceOwner(owner)
            || nonGenericClasses.Contains(owner.Name) && !IsValue(owner);
        internal bool PlainLocalClass(TypeNode component) => component is TypeNode.Fqn { Args: null } owner
            && nonGenericClasses.Contains(owner.Name) && !IsValue(owner);
        internal readonly Dictionary<string, string> CarrierClasses = new(StringComparer.Ordinal);
        readonly Dictionary<(TypeNode, TypeNode), bool> classBases = new();
        internal bool CarrierTarget(TypeNode.Fqn target, TypeNode.Fqn carrier) =>
            HasDeclaredCarrier(target, carrier) || CarrierClassBase(target, carrier);

        bool CarrierClassBase(TypeNode.Fqn target, TypeNode.Fqn carrier)
        {
            if (classBases.TryGetValue((target, carrier), out var cached)) return cached;
            // The interface itself does not establish this class on the CLR stack.
            // Prove the base from the carrier's recorded class, leaving every
            // unknown argument open rather than inventing a closed application.
            static TypeNode.Fqn Open(string name, int arity) =>
                new(name, arity == 0 ? null : Enumerable.Repeat<TypeNode>(new TypeNode.Star(), arity).ToArray());
            TypeNode.Fqn owner = null;
            if (CarrierClasses.TryGetValue(carrier.Name, out var name) && types.TryGetValue(name, out var def))
                owner = Open(name, def.Arity);
            else if (References?.ExistentialSourceType(carrier.Name) is { } source)
                owner = Open(References.RecordedPhysicalTypeName(source.Name), source.Args?.Length ?? 0);
            return classBases[(target, carrier)] = owner != null && !IsValue(target) &&
                (types.TryGetValue(target.Name, out var dest) ? dest.Kind == "class" : References?.IsClassType(target) == true)
                && SupertypeGraph.Reaches(owner, target, types, References);
        }
        internal bool IsStackOnly(TypeNode type) => type is TypeNode.Fqn named
            && !(types.TryGetValue(named.Name, out var local) && local.Arity == (named.Args?.Length ?? 0))
            && References?.IsByRefLikeFqn(named) == true;
        internal bool HasDeclaredCarrier(TypeNode.Fqn target, TypeNode.Fqn source) =>
            declaredCarriers.Contains((target.Name, source.Name)) || ReferencedCarrier?.Invoke(target, source) == true
            || declaredCarriers.Any(edge => edge.Class == target.Name
                && SupertypeGraph.Reaches(source, new TypeNode.Fqn(edge.Carrier), types, References))
            || References?.TryExistentialPhysicalOwner(target.Name, out var carrier) == true
                && SupertypeGraph.Reaches(source, new TypeNode.Fqn(carrier), types, References);
        internal bool NarrowsReference(TypeNode source, TypeNode target)
        {
            source = SignatureValueTypes.Of(source);
            target = SignatureValueTypes.Of(target);
            // CLR reference-array covariance is directional. A Kotlin carrier[]
            // reaching an exact native G<T>[] slot needs the same checked
            // narrowing as its component, never an unchecked pass or a copy.
            if (source is TypeNode.Array fromArray && target is TypeNode.Array toArray
                && fromArray.SzArray == toArray.SzArray && fromArray.Rank == toArray.Rank)
                return NarrowsReference(fromArray.Elem, toArray.Elem);
            if (source == target || source is not TypeNode.Fqn from || target is not TypeNode.Fqn to
                || !IsReferenceSlot(from, IsValue) || !IsReferenceSlot(to, IsValue)) return false;
            if (!referenceNarrowing.TryGetValue((source, target), out var result))
                referenceNarrowing[(source, target)] = result = SupertypeGraph.Reaches(to, from, types, References);
            return result;
        }
        internal TypeNode[] ClosureParameters(BirNewClosure expression)
        {
            if (expression.ClosureType.OrDefault() is not TypeNode.Fqn owner
                || !closureConstructors.TryGetValue(owner.Name, out var parameters)) return null;
            var arguments = expression.TypeArgs.OrDefault() ?? owner.Args;
            return parameters.Select(p => Close(p, arguments, null)).ToArray();
        }
        internal MethodShape Method(BirGraphValue call)
        {
            var owner = CallOwner(call);
            var name = MethodName(call);
            if (owner == null || name == null || !methods.TryGetValue(Key(owner.Name, name), out var candidates)) return null;
            var arguments = MethodArgs(call);
            var wanted = Signature(call);
            var matches = candidates.Where(c => c.Arity == arguments.Length
                && (wanted == null || c.Parameters.Length == wanted.Length
                    && (c.Parameters.SequenceEqual(wanted)
                        || c.Parameters.Select((p, i) => Close(p, owner.Args, arguments) == wanted[i]).All(x => x)))).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }

    sealed class Scope
    {
        internal readonly Dictionary<string, TypeNode> Locals;
        internal readonly TypeNode Return, Owner;
        internal bool NativeArrayReturn, ProjectedCallable;
        internal HashSet<string> ManagedAddresses = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, TypeNode> TemporaryFields, TemporaryLocals;
        internal Scope(TypeNode owner = null, TypeNode ret = null, Dictionary<string, TypeNode> locals = null,
            Dictionary<string, TypeNode> temporaryFields = null, Dictionary<string, TypeNode> temporaryLocals = null)
        {
            Owner = owner; Return = ret;
            Locals = locals ?? new(StringComparer.Ordinal);
            TemporaryFields = temporaryFields ?? new(StringComparer.Ordinal);
            TemporaryLocals = temporaryLocals ?? new(StringComparer.Ordinal);
        }
        internal Scope Copy() => new(Owner, Return, new(Locals, StringComparer.Ordinal), TemporaryFields, TemporaryLocals)
            { NativeArrayReturn = NativeArrayReturn, ProjectedCallable = ProjectedCallable, ManagedAddresses = new(ManagedAddresses, StringComparer.Ordinal) };
        internal static Scope Frame(BirParameter[] parameters, IEnumerable<BirGraphValue> parts, TypeNode owner, TypeNode ret)
        {
            var scope = new Scope(owner, ret);
            foreach (var parameter in parameters)
                if (parameter.Name.HasValue && parameter.Type.HasValue)
                {
                    scope.Locals[parameter.Name.Value] = SignatureValueTypes.Of(parameter.Type.Value);
                    if (scope.Locals[parameter.Name.Value] is TypeNode.ByRef) scope.ManagedAddresses.Add(parameter.Name.Value);
                }
            var all = parts.ToArray();
            bool changed;
            do { changed = false; foreach (var part in all) changed |= CollectTemporaryValues(part, scope.TemporaryFields, scope.TemporaryLocals); }
            while (changed);
            return scope;
        }
    }

    static void RewriteType(IBirTypeDeclaration declaration, Index index)
    {
        var type = Shape(declaration);
        RewriteMembers(type.Fields, type.Constructors, type.Methods,
            type.Name == null ? null : new TypeNode.Fqn(type.Name), index);
    }

    static void RewriteMembers(BirFieldDeclaration[] fields, BirConstructor[] constructors,
        BirFunctionDeclaration[] methods, TypeNode owner, Index index)
    {
        foreach (var field in fields)
            if (field.Init.HasValue && field.Type.HasValue)
            {
                var scope = new Scope(owner);
                var value = Rewrite(Graph(field.Init.Value), scope, index);
                field.Init = Field(Expression(index.ReferenceBuild ? value : Coerce(value, field.Type.Value, scope, index)));
            }
        foreach (var constructor in constructors)
        {
            if (!index.ReferenceBuild)
            {
                var prefix = constructor.PreStmts.OrEmpty().ToList();
                AllocateNativeLocals(constructor.PreStmts.OrEmpty().Cast<BirGraphValue>()
                    .Concat((constructor.ThisArgs.OrDefault() ?? constructor.BaseArgs.OrEmpty()).Cast<BirGraphValue>())
                    .Concat(constructor.Body.OrEmpty().Cast<BirGraphValue>()), constructor.Params.OrEmpty(), prefix, index.ArrayNames);
                if (constructor.PreStmts.IsPresent || prefix.Count != 0) constructor.PreStmts = prefix.ToArray();
            }
            var scope = Scope.Frame(constructor.Params.OrEmpty(),
                constructor.PreStmts.OrEmpty().Cast<BirGraphValue>().Concat(constructor.Body.OrEmpty().Cast<BirGraphValue>()), owner, Void);
            RewriteStatements(constructor.PreStmts.OrEmpty(), scope, index);
            var arguments = constructor.ThisArgs.OrDefault() ?? constructor.BaseArgs.OrDefault();
            RewriteExpressions(arguments, scope, index);
            if (!index.ReferenceBuild)
            {
                var signature = ConstructorParameterTypes(constructor);
                CoerceVector(arguments, signature, scope, index, constructor.DelegationSig.OrDefault());
                var selected = constructor.BaseCtorRef.OrDefault();
                if (IsForeignOwner(selected?.DeclaringType.OrDefault(), index) &&
                    StageNativeArrayOperands(constructor, selected, arguments, signature, scope, index, foreign: true) is { } preamble)
                    constructor.PreStmts = constructor.PreStmts.OrEmpty().Concat(preamble).ToArray();
            }
            RewriteStatements(constructor.Body.OrEmpty(), scope, index);
        }
        foreach (var method in methods)
        {
            var scope = Scope.Frame(method.Params.OrEmpty(), method.Body.OrEmpty().Cast<BirGraphValue>(), owner, method.Ret.OrDefault());
            scope.ProjectedCallable = method.Attrs.OrEmpty().Any(a => a.Attr.OrDefault() is TypeNode.Fqn { Name: RoundtripMetadata.AKProjectedMethod });
            scope.NativeArrayReturn = MayHoldArray(scope.Return, index) &&
                (method.SourceClrBoundary || method.Overrides.OrEmpty().Select(o => o.Owner.OrDefault())
                    .Concat(method.ClrInterfaceImpls.OrEmpty().Concat(method.ClrBaseImpls.OrEmpty()).Select(o => o.Owner.OrDefault()))
                    .Any(o => IsForeignOwner(o, index)));
            if (method.Body.HasValue)
            {
                if (!index.ReferenceBuild)
                {
                    var body = method.Body.Value.ToList();
                    AllocateNativeLocals(method.Body.Value.Cast<BirGraphValue>(), method.Params.OrEmpty(), body, index.ArrayNames);
                    method.Body = body.ToArray();
                }
                RewriteStatements(method.Body.Value, scope, index);
            }
        }
    }

    static void RewriteStatements(IBirStatement[] statements, Scope scope, Index index)
    {
        foreach (ref var statement in statements.AsSpan())
        {
            statement = (IBirStatement)Rewrite((BirGraphValue)statement, scope, index);
            if (statement is BirVariable { Name.HasValue: true, Type.HasValue: true } variable)
                scope.Locals[variable.Name.Value] = variable.Type.Value;
        }
    }
    static void RewriteExpressions(IBirExpression[] expressions, Scope scope, Index index)
    {
        if (expressions == null) return;
        for (var i = 0; i < expressions.Length; i++) expressions[i] = Expression(Rewrite(Graph(expressions[i]), scope, index));
    }

    static BirCast Cast(BirGraphValue expression, TypeNode type) => new() { Type = type, E = Field(Expression(expression)) };
    static IBirExpression CoerceExpression(IBirExpression value, TypeNode target, Scope scope, Index index, TypeNode selected = null)
    {
        if (value == null) return null;
        if (target is TypeNode.ByRef or TypeNode.Mod && SignatureValueTypes.Of(target) is TypeNode.ByRef to)
        {
            var location = value is BirByRefOf address ? address.Inner.OrDefault() : value;
            if (location is BirLocal or BirFieldGet or BirStaticFieldGet or BirByRefLoad
                && ExprType(Graph(location), scope, index) is { } storage)
                CheckNullableLocation(storage is TypeNode.ByRef pointer ? pointer.Of : storage, to.Of);
            if (scope.ProjectedCallable && ManagedAddress(location, scope, index) is { Of: TypeNode.Tv } from && from.Of != to.Of)
                return CheckedAddressView(value, from, to, index);
        }
        // A nominal function's ordinary callable view and the selected CLR
        // parameter can be different constructed delegates, not merely different names.
        if (FunctionSlot(target, selected, index) != target && Underlying(selected) is TypeNode.Fn selectedFunction
            && Underlying(target) is TypeNode.Fqn slot && !index.ReferenceBuild)
        {
            var operand = Graph(value);
            var physical = PhysicalResult(operand, scope, index);
            if (physical is TypeNode.Fqn exact && ClrMemberResolution.SameInterfaceSlotType(exact, slot)) return value;
            var source = physical is TypeNode.Fqn && ClrMemberResolution.PhysicalFunctionShape(index.References, physical) is { } native
                ? native : ExprType(operand, scope, index);
            if (Underlying(source) is TypeNode.Fn { Suspend: false } function
                && function.DelegateParams.Length == selectedFunction.DelegateParams.Length)
                return index.DelegateAdapters.AdaptFunctionView(index.Document, value, selectedFunction, slot, function);
            if (FunctionViewOperand(Graph(value), source, selected, index) is IBirExpression view)
                return index.DelegateAdapters.AdaptFunctionView(index.Document, view, selectedFunction, slot);
        }
        return Expression(Coerce(Graph(value), target, scope, index));
    }

    // A fixed ref slot cannot accept a covariant reference or a converted
    // temporary. Reuse the pointer only when its actual CLR element is identical.
    static IBirExpression CheckedAddressView(IBirExpression value, TypeNode.ByRef from, TypeNode.ByRef to, Index index)
    {
        var refs = index.References;
        var failure = refs.ResolveRefType("System.InvalidCastException");
        var mismatch = new BirBinaryOperation { Op = "!=", Lhs = new BirClassReference { Type = from.Of },
            Rhs = new BirClassReference { Type = to.Of } };
        var fail = new BirThrow { Value = new BirNewClr { Type = new TypeNode.Fqn(failure.FullName), Args = Array.Empty<IBirExpression>(),
            MemberRef = BirMemberReference.FromPhysical(ClrMemberResolution.MemberRefOf(null, refs,
                failure.GetConstructor(System.Type.EmptyTypes), "ctor", failure, null)) } };
        var statements = new List<IBirStatement>();
        if (value is BirByRefOf { Inner.Value: BirLocal local }) value = local;
        if (value is not BirLocal)
        {
            var name = index.ArrayNames.Next("$fixedAddress$");
            statements.Add(new BirVariable { Name = name, Type = from, Init = Field(value) });
            value = new BirLocal { Name = name };
        }
        statements.Add(new BirIf { Branches = new[] { new BirBranch { Cond = mismatch, Body = new IBirStatement[] { fail } } } });
        var view = index.ReferenceView(from.Of, to.Of, value);
        return new BirValueBlock { Type = to, Stmts = statements.ToArray(),
            Result = view };
    }
    static void CoerceVector(IBirExpression[] values, TypeNode[] targets, Scope scope, Index index,
        TypeNode[] selected = null, BirGraphValue nativeCall = null)
    {
        if (values == null || targets == null || values.Length != targets.Length) return;
        for (var i = 0; i < values.Length; i++) values[i] = CoerceExpression(values[i], targets[i], scope, index,
            nativeCall?.NativeFunctionParameters?[i] is {} function
                ? Close(function, OwnerArgs(Member(nativeCall)), MethodArgs(nativeCall))
                : selected?.Length == values.Length ? selected[i] : null);
    }
    static void CoerceArray(IBirExpression[] values, TypeNode target, Scope scope, Index index)
    {
        if (values == null || target == null) return;
        for (var i = 0; i < values.Length; i++) values[i] = CoerceExpression(values[i], target, scope, index);
    }
    static bool NeedsConversion(TypeNode source, TypeNode target, Index index) =>
        PhysicalValueCoercion.NeedsConversion(source, target, index.IsValue,
            index.IsExistentialOwner, index.IsNativeReference, index.CarrierTarget)
        || index.NarrowsReference(source, target);

    static BirGraphValue Coerce(BirGraphValue value, TypeNode target, Scope scope, Index index)
    {
        target = SignatureValueTypes.Of(target);
        if (target != null && value is BirConditional { Type.HasValue: false } conditional
            && (Underlying(target) is TypeNode.Fn { Suspend: false }
                || NeedsConversion(ExprType(Graph(conditional.Then.OrDefault()), scope, index), target, index)
                || NeedsConversion(ExprType(Graph(conditional.Else.OrDefault()), scope, index), target, index)))
        {
            if (conditional.Then.HasValue) conditional.Then = Field(CoerceExpression(conditional.Then.Value, target, scope, index));
            if (conditional.Else.HasValue) conditional.Else = Field(CoerceExpression(conditional.Else.Value, target, scope, index));
            conditional.Type = target;
            return conditional;
        }
        var got = ExprType(value, scope, index);
        if (value is IBirExpression expression
            && index.DelegateAdapters?.AdaptUnitDelegateValue(index.LocalCarriers, index.Document, expression, got, target) is { } adapter)
            return Graph(adapter);
        if (FunctionViewOperand(value, got, target, index) is { } view) return Cast(view, target);
        return NeedsConversion(got, target, index) ? Cast(value, target) : value;
    }

    static TypeNode Underlying(TypeNode type) => type switch {
        TypeNode.Nullable n => Underlying(n.Of), TypeNode.Oblivious o => Underlying(o.Of),
        TypeNode.Mod m => Underlying(m.Of), _ => type,
    };

    // A nullable value is a different addressable slot, not a conversion of
    // the value read from that slot. Obliviousness itself has no CLR identity.
    static bool DifferentNullableStorage(TypeNode left, TypeNode right)
    {
        TypeNode Slot(TypeNode type) => type is TypeNode.Oblivious oblivious ? Slot(oblivious.Of)
            : BirTypeLowering.CanonicalPhysicalSlotType(SignatureValueTypes.Of(type));
        var a = Slot(left);
        var b = Slot(right);
        return a is TypeNode.Nullable an && Slot(an.Of) == b || b is TypeNode.Nullable bn && a == Slot(bn.Of);
    }

    static void CheckNullableLocation(TypeNode storage, TypeNode requested)
    {
        if (DifferentNullableStorage(storage, requested))
            throw new InvalidOperationException($"Cannot pass this location by reference: its CLR storage type '{FieldLegality.Render(storage)}' differs from the required pointee '{FieldLegality.Render(requested)}'");
    }

    // Explicit casts, assignments, calls and returns must agree on what a Kotlin
    // function value is. In particular a nominal object is never itself a delegate.
    static BirGraphValue FunctionViewOperand(BirGraphValue value, TypeNode source, TypeNode target, Index index)
    {
        if (index.References == null || index.ReferenceBuild || value == null || source == null) return null;
        var from = Underlying(source); var to = Underlying(target);
        if (to is TypeNode.Tv slot && from != to && (from is TypeNode.Tv or TypeNode.Fn { Suspend: false }
            || from is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" }))
        {
            if (value is BirCallStatic callSite
                && callSite.Owner.OrDefault() is TypeNode.Fqn { Name: FunctionValueRepresentation.FunctionRuntime }
                && callSite.Method.OrDefault() == "kotlinFunctionSlot" && callSite.Args.OrDefault() is [_, BirClassReference witness]
                && witness.Type.OrDefault() == slot) return null;
            var original = from is TypeNode.Fn ? RuntimeFunctionCall(value, null, index) : value;
            var owner = new TypeNode.Fqn(FunctionValueRepresentation.FunctionRuntime);
            TypeNode[] signature = [Object, new TypeNode.Fqn("System.Type")];
            var call = new BirCallStatic { Owner = owner, Method = "kotlinFunctionSlot", Sig = signature, Ret = Object,
                Args = new IBirExpression[] { Cast(original, Object), new BirClassReference { Type = slot } } };
            if (!index.LocalFunctionRuntime) call.MemberRef = new BirMemberReference { Kind = "method",
                Assembly = "DotKt.Stdlib", DeclaringType = owner, Name = "kotlinFunctionSlot", GenericArity = 0,
                CallingConvention = "static", ParameterTypes = signature, ReturnType = Object };
            return call;
        }
        if (to is TypeNode.Fn { Suspend: false } function && from is TypeNode.Fqn or TypeNode.Tv)
            return RuntimeFunctionCall(value, function.DelegateParams.Count(), index);
        if (to is TypeNode.Fn { Suspend: false } next && from is TypeNode.Fn { Suspend: false } previous
            && next.DelegateParams.Count() != previous.DelegateParams.Count())
            return RuntimeFunctionCall(RuntimeFunctionCall(value, null, index), next.DelegateParams.Count(), index);
        if (from is TypeNode.Tv && to is TypeNode.Fqn { Name: "object" or "System.Object" or "kotlin.Any" })
            return RuntimeFunctionCall(value, null, index);
        if (from is TypeNode.Fn { Suspend: false } && (to is TypeNode.Fqn nominal
            && nominal.Name is not ("System.Delegate" or "System.MulticastDelegate")
            && !index.References.IsClrDelegate(nominal)))
            return RuntimeFunctionCall(value, null, index);
        return null;
    }

    static BirCallStatic RuntimeFunctionCall(BirGraphValue operand, int? arity, Index index)
        => FunctionValueRepresentation.RuntimeViewCall(Cast(operand, Object), arity, bindExternal: !index.LocalFunctionRuntime);

    internal static void AllocateNativeLocals(IEnumerable<BirGraphValue> body, BirParameter[] parameters,
        List<IBirStatement> prologue, BirTemporaryNames arrayNames = null,
        bool genericParametersOnly = false)
        => AllocateNativeLocals(body, parameters.Cast<BirGraphValue>().ToArray(), prologue, arrayNames, genericParametersOnly);

    // Named functions and suspend lambdas supply their actual declarations to
    // the same storage decision; a lambda parameter is not a fabricated method parameter.
    internal static void AllocateNativeLocals(IEnumerable<BirGraphValue> body, IReadOnlyList<BirGraphValue> parameters,
        List<IBirStatement> prologue, BirTemporaryNames arrayNames = null,
        bool genericParametersOnly = false)
    {
        var required = new Dictionary<BirGraphValue, TypeNode>(ReferenceEqualityComparer.Instance);
        var uses = new Dictionary<BirGraphValue, List<BirGraphValue>>(ReferenceEqualityComparer.Instance);
        var parameterSet = new HashSet<BirGraphValue>(parameters, ReferenceEqualityComparer.Instance);
        var names = parameters.Select(NativeParameterName).ToHashSet(StringComparer.Ordinal);
        void Visit(BirGraphValue node, Dictionary<string, BirGraphValue> declarations)
        {
            if (node == null || node is BirFunctionDeclaration or BirConstructor or BirClosureDefinition) return;
            var usedName = node switch { BirLocal local => local.Name.OrDefault(), BirSetLocal set => set.Name.OrDefault(),
                BirVariable storage => storage.Name.OrDefault(), BirFieldGet field => field.Name.OrDefault(),
                BirSetField field => field.Name.OrDefault(), BirStaticFieldGet field => field.Name.OrDefault(),
                BirStaticFieldSet field => field.Name.OrDefault(), BirFieldSetExpression field => field.Name.OrDefault(),
                BirLateinitGet field => field.Name.OrDefault(), BirPropertyGet property => property.Name.OrDefault(),
                BirPropertySet property => property.Name.OrDefault(), BirMemberReference member => member.Name.OrDefault(),
                _ => null };
            if (usedName != null)
            {
                names.Add(usedName);
                if (node is BirLocal or BirSetLocal && declarations.TryGetValue(usedName, out var declared))
                {
                    if (!uses.TryGetValue(declared, out var references)) uses[declared] = references = new();
                    references.Add(node);
                }
            }
            if (node is BirBlock or BirConditional or BirTry or BirForRange or BirForArray or BirForIn
                || node is BirValueBlock block && !block.TransparentScope.OrDefault())
                declarations = new(declarations, StringComparer.Ordinal);
            var physicalCall = node is BirCallStatic or BirCallInstance or BirConstrainedCall or BirNativeStatic or BirNativeInstance
                or BirGenericStatic or BirGenericInstance or BirNew or BirNewClr;
            var arguments = physicalCall ? Arguments(node) : null;
            var callParameters = arguments != null ? ParameterTypes(node) : null;
            var boundCall = arguments != null && callParameters != null && callParameters.Length == arguments.Length;
            node.VisitChildren(child => Visit(child, declarations));
            if (boundCall)
                for (var i = 0; i < callParameters.Length; i++)
                {
                    if (node.LocationOperandFacts.OrDefault()?.Contains(i) == true) continue;
                    if (SignatureValueTypes.Of(callParameters[i]) is not TypeNode.ByRef reference) continue;
                    var location = arguments[i] is BirByRefOf address ? address.Inner.OrDefault() : arguments[i];
                    if (location is not BirLocal { Name.HasValue: true } local
                        || !declarations.TryGetValue(local.Name.Value, out var declaration)
                        || !PhysicalValueCoercion.NativeLocalEligible(genericParametersOnly,
                            parameterSet.Contains(declaration), reference.Of)
                        || SignatureValueTypes.Of(StorageType(declaration)) is TypeNode.ByRef) continue;
                    var element = SignatureValueTypes.Of(reference.Of);
                    if (DifferentNullableStorage(StorageType(declaration), element)) continue;
                    if (required.TryGetValue(declaration, out var previous) && previous != element)
                        throw new InvalidOperationException($"bir2cir: local '{local.Name.Value}' is required by distinct CLR managed-reference element types");
                    required[declaration] = element;
                }
            if (node is BirVariable { Name.HasValue: true } variable) declarations[variable.Name.Value] = variable;
        }
        var declarations = new Dictionary<string, BirGraphValue>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
            if (NativeParameterName(parameter) is { } name) declarations[name] = parameter;
        foreach (var statement in body.ToArray()) Visit(statement, declarations);
        var prefix = new List<IBirStatement>();
        foreach (var (declaration, type) in required)
        {
            if (!parameterSet.Contains(declaration)) { ((BirVariable)declaration).Type = type; continue; }
            if (SignatureValueTypes.Of(StorageType(declaration)) == type) continue;
            var ordinal = 0;
            string name;
            do { name = "$nativeArgument$" + ordinal++; } while (!names.Add(name));
            foreach (var reference in uses.GetValueOrDefault(declaration) ?? [])
                switch (reference)
                {
                    case BirLocal local: local.Name = name; local.Sty = type; break;
                    case BirSetLocal set: set.Name = name; break;
                }
            prefix.Add(new BirVariable { Name = name, Type = type,
                Init = new BirLocal { Name = NativeParameterName(declaration) } });
        }
        foreach (var statement in prefix) arrayNames?.Reserve((BirGraphValue)statement);
        prologue.InsertRange(0, prefix);
    }

    const string ArrayRuntime = TypedGenericArrayStorageLowering.RuntimeOwner;
    static readonly TypeNode NativeVector = new TypeNode.Fqn("System.Array");
    static readonly TypeNode NativeIndex = new TypeNode.Fqn("System.Int32");

    static BirCallStatic ArrayRuntimeCall(string method, TypeNode result, TypeNode[] signature, Index index,
        params IBirExpression[] operands)
    {
        return TypedGenericArrayStorageLowering.Runtime(method, result, signature, !index.LocalArrayRuntime, operands);
    }

    static BirCast AllocateWithWitness(IBirExpression witness, TypeNode.Array target, IBirExpression size, Index index)
    {
        var token = new TypeNode.Fqn("System.Type");
        return Cast(ArrayRuntimeCall("allocateProjectedArray", NativeVector, [token, token, NativeIndex], index,
            witness, new BirClassReference { Type = target.Elem }, size), target);
    }

    static BirGraphValue SelectNativeArray(BirGraphValue allocation, TypeNode.Array target, Scope scope, Index index,
        IBirExpression witness = null)
    {
        BirConstant Int(int value) => new() { Type = NativeIndex, Value = new BirLiteral.IntegerLiteral(value) };
        BirVariable Var(string hint, TypeNode type, IBirExpression value) => new() {
            Name = index.ArrayNames.Next(hint), Type = type, Init = Field(value) };
        BirLocal Read(BirVariable slot) => new() { Name = slot.Name, Sty = slot.Type };
        BirCast Register(IBirExpression size) => witness != null ? AllocateWithWitness(witness, target, size, index) :
            Cast(ArrayRuntimeCall("registerProjectedArray", NativeVector,
            [NativeVector], index, new BirNewArraySized { Elem = target.Elem, Size = Field(size) }), target);
        BirExpressionStatement Store(BirVariable slot, IBirExpression pos, IBirExpression value) => new() {
            Expr = ArrayRuntimeCall("writeProjectedArray", Void, [NativeVector, NativeIndex, Object],
                index, Read(slot), pos, Cast(Graph(value), Object)) };

        if (allocation is BirNewArraySized sized) return Register(sized.Size.Value);
        if (allocation is BirNewArray literal)
        {
            var elements = literal.Elems.OrEmpty();
            var array = Var("$arrayStorage$", target, Register(Int(elements.Length)));
            var statements = new List<IBirStatement> { array };
            for (var i = 0; i < elements.Length; i++) statements.Add(Store(array, Int(i), elements[i]));
            return new BirValueBlock { Type = target, Stmts = statements.ToArray(), Result = Read(array) };
        }

        var init = (BirNewArrayInit)allocation;
        var invoke = init.InvokeRef.Value;
        var fnType = init.FuncType.OrDefault() ?? ExprType(Graph(init.Init.Value), scope, index);
        var parameter = Close(invoke.ParameterTypes.Value.Single(), OwnerArgs(invoke), []);
        var size = Var("$arraySize$", NativeIndex, init.Size.Value);
        var fn = Var("$arrayInit$", fnType, init.Init.Value);
        var storage = Var("$arrayStorage$", target, Register(Read(size)));
        var ordinal = index.ArrayNames.Next("$arrayIndex$");
        BirLocal Position() => new() { Name = ordinal, Sty = NativeIndex };
        var element = new BirDelegateInvoke { FuncType = fnType, InvokeRef = invoke, Recv = Read(fn),
            Args = new IBirExpression[] { Cast(Position(), parameter) } };
        return new BirValueBlock { Type = target, Stmts = new IBirStatement[] { size, fn, storage, new BirFor {
            Var = ordinal, From = Int(0), To = Read(size), Cmp = "<", Step = 1,
            Body = new IBirStatement[] { Store(storage, Position(), element) } } }, Result = Read(storage) };
    }

    static BirGraphValue PackLogicalSpread(BirSpreadConcat spread, Index index, IBirExpression witness = null)
    {
        var flag = new TypeNode.Fqn("System.Boolean");
        var vector = new TypeNode.Array(spread.Elem.Value);
        var builder = new BirVariable { Name = index.ArrayNames.Next("$spread$"), Type = Object,
            Init = Field(ArrayRuntimeCall("newProjectedSpread", Object, [], index)) };
        BirLocal Local() => new() { Name = builder.Name, Sty = Object };
        var statements = new List<IBirStatement> { builder };
        foreach (var part in spread.Parts.OrEmpty())
            statements.Add(new BirExpressionStatement { Expr = ArrayRuntimeCall("appendProjectedSpread", Void,
                [Object, Object, flag], index, Local(), Cast(Graph(part.E.Value), Object),
                new BirConstant { Type = flag, Value = new BirLiteral.BooleanLiteral(part.Spread.OrDefault()) }) });
        var length = ArrayRuntimeCall("projectedSpreadSize", NativeIndex, [Object], index, Local());
        IBirExpression allocation = witness != null ? AllocateWithWitness(witness, vector, length, index)
            : new BirNewArraySized { Elem = vector.Elem, Size = Field(length) };
        var packed = ArrayRuntimeCall("finishProjectedSpread", NativeVector, [Object, NativeVector], index, Local(), allocation);
        return new BirValueBlock { Type = vector, Stmts = statements.ToArray(), Result = Field(Cast(packed, vector)) };
    }

    static string NativeParameterName(BirGraphValue declaration) => declaration switch {
        BirParameter parameter => parameter.Name.OrDefault(),
        BirLambdaParameter parameter => parameter.Name.OrDefault(),
        _ => throw new InvalidOperationException("Native parameter storage requires an actual parameter declaration"),
    };

    static TypeNode StorageType(BirGraphValue declaration) => declaration switch {
        BirVariable variable => variable.Type.OrDefault(), BirParameter parameter => parameter.Type.OrDefault(),
        BirLambdaParameter parameter => parameter.Type.OrDefault(), _ => null,
    };

    static BirGraphValue Rewrite(BirGraphValue node, Scope scope, Index index, bool resultUsed = true, bool location = false)
    {
        if (node == null) return null;
        // Kotlin Array.set returns Unit. Both a native store and a projected store must
        // perform the write before a consumer (including a null comparison) observes that value.
        if (resultUsed && !index.ReferenceBuild && node is BirArraySet)
            return UnitAfter(Rewrite(node, scope, index, resultUsed: false), index);
        // Deferred declarations never inherit the caller's locals or return slot.
        if (node is BirFunctionDeclaration method)
        {
            RewriteStatements(method.Body.OrEmpty(), Scope.Frame(method.Params.OrEmpty(),
                method.Body.OrEmpty().Cast<BirGraphValue>(), scope.Owner, method.Ret.OrDefault()), index);
            return method;
        }
        if (node is BirClosureDefinition closure)
        {
            var frame = Scope.Frame([], closure.Body.OrEmpty().Cast<BirGraphValue>(), scope.Owner, closure.Ret.OrDefault());
            foreach (var parameter in closure.Params.OrEmpty())
                if (parameter.Name.HasValue && parameter.Type.HasValue)
                    frame.Locals[parameter.Name.Value] = SignatureValueTypes.Of(parameter.Type.Value);
            RewriteStatements(closure.Body.OrEmpty(), frame, index);
            return closure;
        }
        if (!index.ReferenceBuild && node is BirValueBlock { FreshArrayWitness: true } request)
        {
            var allocation = Graph(request.Result.Value);
            var witness = ((BirExpressionStatement)request.Stmts.Value.Single()).Expr.Value;
            RewriteChildren(allocation, scope, index, resultUsed);
            if (allocation is BirSpreadConcat pack) return PackLogicalSpread(pack, index, witness);
            var componentType = allocation switch { BirNewArray a => a.Elem.Value, BirNewArraySized a => a.Elem.Value,
                BirNewArrayInit a => a.Elem.Value, _ => throw new InvalidOperationException("Missing fresh array allocation") };
            return SelectNativeArray(allocation, new TypeNode.Array(componentType), scope, index, witness);
        }
        RewriteChildren(node, scope, index, resultUsed);
        if (node is BirBinaryOperation { Op.Value: "===" } identity) return LowerIdentity(identity, scope, index);
        if (node is BirSpreadConcat { LogicalSpreadOperands: true } spread)
            return PackLogicalSpread(spread, index);
        if (location && node is BirArrayGet) return node;
        // Staging a native invocation must not turn its ref result into a
        // temporary value. Take the original invocation's address *after*
        // the publication preamble, in the same evaluation scope.
        if (node is BirByRefOf address && address.Inner.OrDefault() is BirValueBlock block &&
            block.Result.OrDefault() is BirGraphValue call && Member(call)?.ReturnType.OrDefault() is TypeNode.ByRef returnedAddress)
        {
            address.Inner = block.Result;
            block.Result = address;
            block.Type = PhysicalResult(call, scope, index) ?? returnedAddress;
            return block;
        }
        if (node is BirVariable alias && alias.Type.OrDefault() is TypeNode.ByRef &&
            ManagedAddress(alias.Init.OrDefault(), scope, index) is { } exact)
        {
            alias.Type = exact;
            scope.ManagedAddresses.Add(alias.Name.Value);
        }

        var allocated = node switch { BirNewArray literal => literal.Elem.OrDefault(), BirNewArrayInit init => init.Elem.OrDefault(),
            BirNewArraySized sized => sized.Elem.OrDefault(), _ => null };
        static TypeNode ComponentLeaf(TypeNode type)
        {
            while (type is TypeNode.Array a) type = a.Elem;
            return type;
        }
        if (!index.ReferenceBuild && (allocated is TypeNode.Array or TypeNode.Fqn { Name: "System.Array" } ||
            ComponentLeaf(allocated) is TypeNode.Fqn { Args.Length: > 0 } component &&
            (index.LocalCarriers?.ContainsKey(component.Name) == true ||
             index.References?.TryExistentialPhysicalOwner(component.Name, out _) == true)))
            return SelectNativeArray(node, new TypeNode.Array(allocated), scope, index);
        var array = node switch { BirArrayGet value => value.Array.OrDefault(), BirArraySet value => value.Array.OrDefault(),
            BirArrayLength value => value.Array.OrDefault(), BirForArray value => value.Array.OrDefault(), _ => null };
        if (index.References != null && array != null)
        {
            var element = node switch { BirArrayGet read => read.Elem.OrDefault(), BirArraySet write => write.Elem.OrDefault(),
                BirForArray loop => loop.Elem.OrDefault(), _ => null };
            // An unchecked widening to a reference array preserves the vector,
            // not its statically declared component. An unsealed base/interface
            // slot may therefore read or write a projected vector as well.
            var leaf = ComponentLeaf(element);
            if (leaf is TypeNode.Tv || leaf is TypeNode.Fqn application &&
                (index.IsExistentialOwner(application) || !index.IsValue(application) &&
                    index.References.PhysicalTypeForProof(application.Name, application.Args?.Length ?? 0)?.IsSealed != true))
            {
                var plain = index.PlainLocalClass(element);
                IBirExpression ReadAt(IBirExpression vector, IBirExpression position) => plain
                    ? TypedGenericArrayStorageLowering.PlainClassAccess(vector, position, null, element, index.ArrayNames, !index.LocalArrayRuntime)
                    : TypedGenericArrayStorageLowering.Read(vector, position, element, !index.LocalArrayRuntime);
                switch (node)
                {
                    case BirArrayGet read: return Graph(ReadAt(array, read.Index.Value));
                    case BirArraySet write: return plain
                        ? TypedGenericArrayStorageLowering.PlainClassAccess(array, write.Index.Value, write.Value.Value,
                            element, index.ArrayNames, !index.LocalArrayRuntime)
                        : TypedGenericArrayStorageLowering.Store(write, !index.LocalArrayRuntime);
                    case BirForArray loop: return TypedGenericArrayStorageLowering.Loop(loop, array, index.References, index.ArrayNames, ReadAt);
                }
            }
            var rewritten = TypedGenericArrayStorageLowering.LowerOperation(node, index.References,
                index.ArrayNames, ExprType(Graph(array), scope, index), external: !index.LocalArrayRuntime);
            if (!ReferenceEquals(rewritten, node)) return Rewrite(rewritten, scope, index, resultUsed);
        }
        if (node.LocationOperandFacts.OrDefault() is { } positions)
        {
            var operands = Arguments(node);
            var actual = node.ApplicationTypeArguments.Value;
            for (int i = 0; i < positions.Length; i++)
            {
                var operand = operands[positions[i]];
                var value = operand is BirByRefOf pointer ? pointer.Inner.Value : operand;
                var type = (value as BirArrayGet)?.Elem.OrDefault() ?? ManagedAddress(operand, scope, index)?.Of ?? ExprType(Graph(value), scope, index);
                actual[actual.Length - positions.Length + i] = type is TypeNode.ByRef p ? p.Of :
                    type ?? throw new InvalidOperationException("Projected argument has no physical location type");
            }
            node.LocationOperandFacts = default;
        }
        if (node is BirByRefLoad load)
        {
            var pointer = load.Ptr.OrDefault() ?? (load.Local.HasValue ? new BirLocal { Name = load.Local.Value } : null);
            if (ExprType(Graph(pointer), scope, index) is TypeNode.ByRef reference)
            {
                if (load.Elem.HasValue) CheckNullableLocation(reference.Of, load.Elem.Value);
                load.Elem = reference.Of;
            }
        }
        if (node is BirByRefStore store)
        {
            var pointer = store.Ptr.OrDefault() ?? (store.Local.HasValue ? new BirLocal { Name = store.Local.Value } : null);
            if (ExprType(Graph(pointer), scope, index) is TypeNode.ByRef reference) store.Elem = reference.Of;
        }
        if (node is BirConstrainedCall constrained && constrained.RecvType.OrDefault() is TypeNode.Tv)
        {
            if (ExprType(Graph(constrained.Recv.OrDefault()), scope, index) is TypeNode.Tv receiverVariable)
                constrained.RecvType = receiverVariable;
            if ((Member(node)?.DeclaringType.OrDefault() ?? constrained.Iface.OrDefault()) is TypeNode.Fqn referenceOwner
                && index.ExactReferenceOwner(referenceOwner))
            {
                constrained.RecvType = referenceOwner;
                if (constrained.Recv.HasValue) constrained.Recv = Field(CoerceExpression(constrained.Recv.Value, referenceOwner, scope, index));
            }
        }
        if (!index.ReferenceBuild && node is BirVariable variable && variable._callEvalTemporary.IsPresent)
        {
            variable._callEvalTemporary = default;
            if (!index.ReferenceBuild && variable.Type.OrDefault() is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" })
            {
                var constructed = variable.Name.HasValue && scope.TemporaryLocals.TryGetValue(variable.Name.Value, out var known)
                    ? known : ConstructedReferenceType(Graph(variable.Init.OrDefault()), scope.TemporaryFields, scope.TemporaryLocals);
                if (constructed is TypeNode.Fqn) variable.Type = constructed;
            }
        }
        CoerceInputs(node, scope, index);
        if (index.ReferenceBuild) return node;
        if (IsVoid(scope.Return))
        {
            if (node is BirReturn { Value.HasValue: true } exit)
            {
                var effect = new BirExpressionStatement { Expr = exit.Value };
                exit.Value = default;
                return new BirBlock { Body = new IBirStatement[] { effect, exit } };
            }
            if (node is BirReturnExpression { Value.HasValue: true } expression)
            {
                var effect = new BirExpressionStatement { Expr = expression.Value };
                expression.Value = default;
                return new BirValueBlock { Type = Void, Stmts = new IBirStatement[] { effect }, Result = expression };
            }
        }
        var result = CoerceDeclaredResult(node, scope, index, resultUsed);
        if (node is BirFieldGet field && TemporaryFieldKey(field) is { } fieldKey
            && scope.TemporaryFields.TryGetValue(fieldKey, out var retained)
            && ExprType(field, scope, index) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" })
            return Cast(field, retained);
        if (resultUsed && node is BirConstant && IsVoid(ExprType(result, scope, index))) return Graph(index.UnitValue());
        if (resultUsed && node is BirCallStatic or BirCallInstance or BirConstrainedCall or BirNativeStatic or BirNativeInstance
                or BirGenericStatic or BirGenericInstance or BirDelegateInvoke or BirConditional or BirValueBlock
            && IsVoid(ExprType(result, scope, index)))
            return UnitAfter(ExposeNativeArrayArguments(node, result, scope, index), index);
        return ExposeNativeArrayArguments(node, result, scope, index);
    }

    static BirValueBlock UnitAfter(BirGraphValue effect, Index index) => new() { Type = new TypeNode.Fqn("kotlin.Unit"),
        Stmts = new IBirStatement[] { new BirExpressionStatement { Expr = Field(Expression(effect)) } }, Result = Field(index.UnitValue()) };

    // Preserve the invocation's operand order: an exception from a later
    // argument must not publish an earlier array. Retain the converted values
    // first, then expose the same vectors immediately before the native call.
    static BirGraphValue ExposeNativeArrayArguments(BirGraphValue call, BirGraphValue result, Scope scope, Index index)
    {
        if (call is not (BirCallStatic or BirCallInstance or BirConstrainedCall or BirNativeStatic or BirNativeInstance or
            BirGenericStatic or BirGenericInstance or BirNew or BirNewClr)) return result;
        var member = Member(call);
        var foreign = call.SourceClrBoundary || call is BirNew && IsForeignOwner(member?.DeclaringType.OrDefault(), index);
        var kotlin = !foreign && call is BirCallStatic or BirCallInstance or BirConstrainedCall;
        if (!kotlin && !foreign && call is not (BirNativeStatic or BirNativeInstance or BirGenericStatic or BirGenericInstance or BirNewClr)) return result;
        var operands = Arguments(call);
        if (kotlin && operands?.Any(a => a is BirArrayGet || a is BirByRefOf ptr && ptr.Inner.OrDefault() is BirArrayGet) != true) return result;
        var statements = StageNativeArrayOperands(call, member, operands, ParameterTypes(call), scope, index, foreign, kotlin);
        return statements == null ? result : new BirValueBlock { Type = ExprType(result, scope, index) ?? Void,
            Stmts = statements, Result = Field(Expression(result)) };
    }

    static bool IsForeignOwner(TypeNode type, Index index) => type is TypeNode.Fqn owner &&
        index.References?.ResolveForeignProjectionType(owner.Name, owner.Args) != null;

    static bool ProjectedArrayOperand(TypeNode type) =>
        type != null && SignatureValueTypes.Of(type) is TypeNode.Array { Elem: TypeNode.Fqn { Args.Length: > 0 } };

    static bool MayHoldArray(TypeNode type, Index index) => MayHoldArray(type, index.References);
    internal static bool MayHoldArray(TypeNode type, ReferenceMetadataIndex refs) => type is TypeNode.Array or TypeNode.Tv ||
        type is TypeNode.Fqn f && (f.Name is "object" or "System.Object" or "System.Array" ||
            refs?.IsInterfaceType(f) == true);

    static IBirStatement[] StageNativeArrayOperands(BirGraphValue call, BirMemberReference member,
        IBirExpression[] args, TypeNode[] signature, Scope scope, Index index, bool foreign, bool locationsOnly = false)
    {
        bool Publishes(TypeNode type) => !locationsOnly && (ProjectedArrayOperand(type) || foreign && MayHoldArray(type, index));
        var receiver = Receiver(call);
        var receiverType = receiver == null ? null : ExprType(Graph(receiver), scope, index);
        var publishesReceiver = foreign && MayHoldArray(receiverType, index);
        args ??= [];
        if (args.Length == 0) signature ??= [];
        BirArrayGet Indexed(int i) => (args[i] is BirByRefOf a ? a.Inner.OrDefault() : args[i]) as BirArrayGet;
        bool ReferenceElement(TypeNode type) => type is not TypeNode.Fqn f || !index.IsValue(f);
        if (signature == null || args.Length != signature.Length || !publishesReceiver &&
            !signature.Where((t, i) => Publishes(t) || SignatureValueTypes.Of(t) is TypeNode.ByRef pointer &&
                (Publishes(pointer.Of) || Indexed(i) != null && ReferenceElement(pointer.Of))).Any())
            return null;
        var statements = new List<IBirStatement>();
        var exports = new List<IBirExpression>();
        var vectors = new List<IBirExpression>();
        var indices = new List<IBirExpression>();
        var dynamicValue = false;
        BirLocal Retain(IBirExpression value, TypeNode type)
        {
            var slot = new BirVariable { Name = index.ArrayNames.Next("$nativeOperand$"), Type = type, Init = Field(value) };
            statements.Add(slot);
            return new BirLocal { Name = slot.Name, Sty = type };
        }
        BirLocal Address(IBirExpression value, TypeNode.ByRef pointer) =>
            Retain(value is BirByRefOf ? value : new BirByRefOf { Inner = Field(value) }, pointer);
        // Only reference instantiations can hold an array. Read their reference
        // slot without a box instruction: even unreachable box !T is invalid
        // for a ref-like T under the unoptimized CLR JIT.
        IBirExpression ExposureCandidate(BirGraphValue value, TypeNode type) => type is not TypeNode.Tv ? Expression(value)
            : new BirConditional { Type = Object, Cond = index.IsValueType(type),
                Then = new BirConstant { Type = Object, Value = new BirLiteral.NullLiteral() },
                Else = new BirByRefLoad { Elem = Object, Ptr = index.ReferenceView(type, Object,
                    value is BirByRefLoad load ? load.Ptr.OrDefault() ?? new BirLocal { Name = load.Local } :
                        Expression(value)) } };
        if (receiver != null)
        {
            if (receiverType == null) return null;
            // Lexical this is already stable, including the managed this of
            // a struct. Keep its exact owner frame on the original call.
            // A method result is a Kotlin value receiver, even if the selected
            // CLR member returns by ref. Snapshot it before evaluating later
            // operands; only a receiver designating storage keeps its address.
            bool location = receiver is BirLocal or BirByRefLoad or BirArrayGet or BirStackGet or BirStaticFieldGet ||
                receiver is BirFieldGet field && field.Member.OrDefault() != "accessor" ||
                receiver is BirPropertyGet property && property.Member.OrDefault() == "field";
            IBirExpression saved = receiver is BirThis ? receiver : location &&
                (receiverType is TypeNode.Fqn named && index.IsValue(named) || receiverType is TypeNode.Tv && call is BirConstrainedCall)
                ? new BirByRefLoad { Local = Address(receiver, new TypeNode.ByRef(receiverType)).Name, Elem = receiverType }
                : Retain(receiver, receiverType);
            SetReceiver(call, saved);
            if (publishesReceiver)
            {
                dynamicValue |= receiverType is not TypeNode.Array;
                exports.Add(ExposureCandidate(Graph(saved).DeepClone(), receiverType));
            }
        }
        for (var i = 0; i < args.Length; i++)
        {
            if (SignatureValueTypes.Of(signature[i]) is TypeNode.ByRef pointer)
            {
                if (Indexed(i) is { } location && ReferenceElement(pointer.Of))
                {
                    var vector = Retain(location.Array.Value, new TypeNode.Array(pointer.Of));
                    var offset = Retain(location.Index.Value, NativeIndex);
                    location.Array = vector;
                    location.Index = offset;
                    vectors.Add((BirLocal)vector.DeepClone());
                    indices.Add((BirLocal)offset.DeepClone());
                }
                var address = Address(args[i], pointer);
                args[i] = address;
                if (Publishes(pointer.Of) && index.References?.IsOutputOnlyArgument(member, i) != true)
                {
                    dynamicValue |= pointer.Of is not TypeNode.Array;
                    var load = new BirByRefLoad { Local = address.Name, Elem = pointer.Of };
                    exports.Add(ExposureCandidate(load, pointer.Of));
                }
            }
            else
            {
                var saved = Retain(args[i], signature[i]);
                args[i] = saved;
                if (Publishes(signature[i]))
                {
                    dynamicValue |= signature[i] is not TypeNode.Array;
                    exports.Add(ExposureCandidate(saved.DeepClone(), signature[i]));
                }
            }
        }
        if (exports.Count != 0 || vectors.Count != 0)
        {
            var batch = new TypeNode.Array(Object);
            var publish = vectors.Count != 0 ? ArrayRuntimeCall("exposeProjectedArguments", Void, [batch, batch, new TypeNode.Array(NativeIndex)], index,
                    new BirNewArray { Elem = Object, Elems = exports.ToArray() },
                    new BirNewArray { Elem = Object, Elems = vectors.ToArray() },
                    new BirNewArray { Elem = NativeIndex, Elems = indices.ToArray() })
                : exports.Count == 1 && !dynamicValue ? ArrayRuntimeCall("exposeProjectedArray", NativeVector, [NativeVector], index, exports[0])
                : exports.Count == 1 ? ArrayRuntimeCall("exposeProjectedValue", Void, [Object], index, exports[0])
                : ArrayRuntimeCall("exposeProjectedArrays", Void, [batch], index, new BirNewArray { Elem = Object, Elems = exports.ToArray() });
            statements.Add(new BirExpressionStatement { Expr = publish });
        }
        return statements.ToArray();
    }

    static void RewriteChildren(BirGraphValue node, Scope scope, Index index, bool resultUsed)
    {
        OptionalField<IBirExpression> Expr(IBirExpression value, bool used = true) => Field(value == null ? null : Expression(Rewrite(Graph(value), scope, index, used)));
        switch (node)
        {
            case BirExpressionStatement statement:
                if (statement.Expr.HasValue) statement.Expr = Expr(statement.Expr.Value, false); return;
            case BirByRefOf address when address.Inner.OrDefault() is BirArrayGet element:
                address.Inner = Field(Expression(Rewrite(element, scope, index, location: true)));
                return;
            case BirReturn exit:
                if (exit.Value.HasValue) exit.Value = Expr(exit.Value.Value, !IsVoid(scope.Return)); return;
            case BirReturnExpression exit:
                if (exit.Value.HasValue) exit.Value = Expr(exit.Value.Value, !IsVoid(scope.Return)); return;
            case BirConditional conditional:
                if (conditional.Cond.HasValue) conditional.Cond = Expr(conditional.Cond.Value);
                if (conditional.Then.HasValue) conditional.Then = Expr(conditional.Then.Value, resultUsed && !IsVoid(conditional.Type.OrDefault()));
                if (conditional.Else.HasValue) conditional.Else = Expr(conditional.Else.Value, resultUsed && !IsVoid(conditional.Type.OrDefault()));
                return;
            case BirValueBlock block:
                RewriteStatements(block.Stmts.OrEmpty(), scope, index);
                if (block.Result.HasValue) block.Result = Expr(block.Result.Value, resultUsed && !IsVoid(block.Type.OrDefault())); return;
            case BirBlock block: RewriteStatements(block.Body.OrEmpty(), scope, index); return;
            case BirBranch branch:
                if (branch.Cond.HasValue) branch.Cond = Expr(branch.Cond.Value);
                RewriteStatements(branch.Body.OrEmpty(), scope, index); return;
            case BirTry statement:
                RewriteStatements(statement.Body.OrEmpty(), scope, index);
                foreach (var clause in statement.Catches.OrEmpty())
                {
                    var inner = scope.Copy();
                    if (clause.Var.HasValue && clause.ExcType.HasValue) inner.Locals[clause.Var.Value] = clause.ExcType.Value;
                    RewriteStatements(clause.Body.OrEmpty(), inner, index);
                }
                RewriteStatements(statement.Finally.OrEmpty(), scope, index); return;
            case BirWhile loop:
                if (loop.Cond.HasValue) loop.Cond = Expr(loop.Cond.Value);
                RewriteStatements(loop.Body.OrEmpty(), scope, index); return;
            case BirFor loop:
                if (loop.From.HasValue) loop.From = Expr(loop.From.Value);
                if (loop.To.HasValue) loop.To = Expr(loop.To.Value);
                RewriteStatements(loop.Body.OrEmpty(), LoopScope(loop.Var.OrDefault(), new TypeNode.Fqn("System.Int32"), scope), index); return;
            case BirForRange loop:
                if (loop.Range.HasValue) loop.Range = Expr(loop.Range.Value);
                RewriteStatements(loop.Body.OrEmpty(), LoopScope(loop.Var.OrDefault(), new TypeNode.Fqn("System.Int32"), scope), index); return;
            case BirForArray loop:
                if (loop.Array.HasValue) loop.Array = Expr(loop.Array.Value);
                RewriteStatements(loop.Body.OrEmpty(), LoopScope(loop.Var.OrDefault(), loop.Elem.OrDefault(), scope), index); return;
            case BirForEach loop:
                if (loop.Src.HasValue) loop.Src = Expr(loop.Src.Value);
                RewriteStatements(loop.Body.OrEmpty(), LoopScope(loop.Var.OrDefault(), loop.Elem.OrDefault(), scope), index); return;
            case BirForIn loop:
                if (loop.Src.HasValue) loop.Src = Expr(loop.Src.Value);
                RewriteStatements(loop.Body.OrEmpty(), LoopScope(loop.Var.OrDefault(), loop.Elem.OrDefault(), scope), index);
                if (loop.Fallback.HasValue) Rewrite(loop.Fallback.Value, scope, index); return;
            default:
                // A CLR ref operand names a location, not the Kotlin value
                // read from it. Consult the selected signature before walking
                // an indexed argument (its receiver and index still evaluate).
                var supplied = Arguments(node);
                var signature = supplied?.Any(a => a is BirArrayGet) == true ? ParameterTypes(node) : null;
                node.VisitChildSlots(slot => {
                    var child = slot.Value;
                    var pos = signature != null && child is BirArrayGet read ? Array.FindIndex(supplied, a => ReferenceEquals(a, read)) : -1;
                    var pointer = pos >= 0 && pos < signature.Length ? SignatureValueTypes.Of(signature[pos]) as TypeNode.ByRef : null;
                    var rewritten = Rewrite(child, scope, index, location: pointer != null);
                    if (pointer != null && rewritten is BirArrayGet indexed)
                    {
                        // ldelema also requires a vector-typed receiver. An open
                        // Kotlin Array<T> is System.Array even when the element
                        // already names exactly the selected ref T.
                        var component = node.LocationOperandFacts.OrDefault()?.Contains(pos) == true
                            ? indexed.Elem.Value : pointer.Of;
                        var vector = new TypeNode.Array(component);
                        var receiver = Graph(indexed.Array.Value);
                        if (ExprType(receiver, scope, index) != vector)
                            indexed.Array = Field(Cast(receiver, vector));
                        indexed.Elem = component;
                    }
                    slot.Replace(rewritten);
                });
                return;
        }
    }

    static Scope LoopScope(string variable, TypeNode type, Scope scope)
    {
        if (variable == null || type == null) return scope;
        var child = scope.Copy(); child.Locals[variable] = type; return child;
    }

    static TypeNode ConstructedReferenceType(BirGraphValue node, IReadOnlyDictionary<string, TypeNode> fields,
        IReadOnlyDictionary<string, TypeNode> locals) => node switch {
        BirValueBlock block => ConstructedReferenceType(Graph(block.Result.OrDefault()), fields, locals),
        BirLocal { Name.HasValue: true } local when locals.TryGetValue(local.Name.Value, out var type) => type,
        BirFieldGet field when TemporaryFieldKey(field) is { } key && fields.TryGetValue(key, out var retained) => retained,
        BirNewList list => list.CtorRef.OrDefault()?.DeclaringType.OrDefault(),
        _ => null,
    };

    static string TemporaryFieldKey(BirGraphValue node) => node switch {
        BirFieldGet { Recv.HasValue: true, OwnerType.HasValue: true, Name.HasValue: true } field when field.Recv.Value is BirThis => field.OwnerType.Value + "\0" + field.Name.Value,
        BirSetField { Recv.HasValue: true, OwnerType.HasValue: true, Name.HasValue: true } field when field.Recv.Value is BirThis => field.OwnerType.Value + "\0" + field.Name.Value,
        _ => null,
    };

    static bool CollectTemporaryValues(BirGraphValue node, Dictionary<string, TypeNode> fields, Dictionary<string, TypeNode> locals)
    {
        if (node == null || node is BirFunctionDeclaration or BirConstructor or BirClosureDefinition) return false;
        var changed = false;
        if (node is BirSetField { _callEvalTemporary.HasValue: true } field && field._callEvalTemporary.Value
            && TemporaryFieldKey(field) is { } key && !fields.ContainsKey(key)
            && ConstructedReferenceType(Graph(field.Value.OrDefault()), fields, locals) is { } type)
        { fields[key] = type; changed = true; }
        if (node is BirVariable { _callEvalTemporary.HasValue: true, Name.HasValue: true } variable && variable._callEvalTemporary.Value
            && !locals.ContainsKey(variable.Name.Value)
            && ConstructedReferenceType(Graph(variable.Init.OrDefault()), fields, locals) is { } localType)
        { locals[variable.Name.Value] = localType; changed = true; }
        node.VisitChildren(child => changed |= CollectTemporaryValues(child, fields, locals));
        return changed;
    }

    static void CoerceInputs(BirGraphValue node, Scope scope, Index index)
    {
        if (node is BirBinaryOperation binary) WidenNumericOperands(binary, scope, index);
        if (index.ReferenceBuild && node is not BirBinaryOperation { Op.HasValue: true, Op.Value: "===" }) return;
        OptionalField<IBirExpression> C(OptionalField<IBirExpression> value, TypeNode target, TypeNode selected = null) => value.HasValue && target != null
            ? OptionalField<IBirExpression>.Of(CoerceExpression(value.Value, target, scope, index, selected)) : value;
        OptionalField<IBirExpression> Cross(OptionalField<IBirExpression> value, TypeNode target, bool native, TypeNode selected = null)
        {
            var converted = C(value, target, selected);
            if (!native || !converted.HasValue) return converted;
            if (target is TypeNode.Array)
                return Field(Cast(ArrayRuntimeCall("exposeProjectedArray", NativeVector, [NativeVector], index, converted.Value), target));
            // An object- or interface-typed slot can hold the same vector.
            // Inspect it dynamically but retain the value and its declared type.
            var slot = new BirVariable { Name = index.ArrayNames.Next("$nativeResult$"), Type = target, Init = converted };
            var local = new BirLocal { Name = slot.Name, Sty = target };
            return Field(new BirValueBlock { Type = target, Stmts = new IBirStatement[] { slot, new BirExpressionStatement {
                Expr = ArrayRuntimeCall("exposeProjectedValue", Void, [Object], index,
                    target is TypeNode.Tv ? Cast(local.DeepClone(), Object) : (BirLocal)local.DeepClone()) } }, Result = local });
        }
        OptionalField<IBirExpression> FieldWrite(OptionalField<IBirExpression> value)
        {
            var target = FieldTarget(node, scope, index);
            return Cross(value, target, MayHoldArray(target, index) && IsForeignOwner(Member(node)?.DeclaringType.OrDefault(), index),
                Signature(node)?.LastOrDefault());
        }
        OptionalField<IBirExpression> AsKotlinObject(OptionalField<IBirExpression> value) =>
            value.HasValue && FunctionViewOperand(Graph(value.Value), ExprType(Graph(value.Value), scope, index), Object, index)
                is { } original ? Field(Expression(original)) : value;
        switch (node)
        {
            case BirObjectMethod value:
                value.Recv = AsKotlinObject(value.Recv); value.Arg = AsKotlinObject(value.Arg);
                ValidateNativePointerConversion(ExprType(Graph(value.Recv.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.Arg.OrDefault()), scope, index), Object); break;
            case BirObjectEquals value:
                value.Lhs = AsKotlinObject(value.Lhs); value.Rhs = AsKotlinObject(value.Rhs);
                ValidateNativePointerConversion(ExprType(Graph(value.Lhs.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.Rhs.OrDefault()), scope, index), Object); break;
            case BirIsInstance value:
                if (FunctionViewOperand(Graph(value.E.OrDefault()), ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault(), index) is { } tested)
                    value.E = Field(Expression(tested));
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault()); break;
            case BirInstanceOrNull value:
                if (FunctionViewOperand(Graph(value.E.OrDefault()), ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault(), index) is { } safe)
                    value.E = Field(Expression(safe));
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault()); break;
            case BirConcat value:
                foreach (var part in value.Parts.OrEmpty()) ValidateNativePointerConversion(ExprType(Graph(part), scope, index), Object); break;
            case BirCast value:
                if (FunctionViewOperand(Graph(value.E.OrDefault()), ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault(), index) is { } converted)
                    value.E = Field(Expression(converted));
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault()); break;
            case BirVariable value: value.Init = C(value.Init, value.Type.OrDefault()); break;
            case BirSetLocal value:
                if (value.Name.HasValue && scope.Locals.TryGetValue(value.Name.Value, out var type)) value.Value = C(value.Value, type); break;
            case BirSetField value: value.Value = FieldWrite(value.Value); break;
            case BirFieldSetExpression value: value.Value = FieldWrite(value.Value); break;
            case BirStaticFieldSet value: value.Value = FieldWrite(value.Value); break;
            case BirReturn value: value.Value = Cross(value.Value, scope.Return, scope.NativeArrayReturn); break;
            case BirReturnExpression value: value.Value = Cross(value.Value, scope.Return, scope.NativeArrayReturn); break;
            case BirValueBlock value: value.Result = C(value.Result, value.Type.OrDefault()); break;
            case BirCallStatic or BirCallInstance or BirConstrainedCall or BirNativeStatic or BirNativeInstance or BirGenericStatic or BirGenericInstance:
                CoerceVector(Arguments(node), ParameterTypes(node), scope, index, Signature(node), node); break;
            case BirNew or BirNewClr: CoerceConstructorArguments(node, scope, index); break;
            case BirNewClosure value: CoerceVector(value.Captures.OrDefault(), index.ClosureParameters(value), scope, index); break;
            case BirDelegateInvoke value: CoerceDelegateArguments(value, scope, index); break;
            case BirPropertySet value: value.Value = FieldWrite(value.Value); break;
            case BirArraySet value:
                // A native location can hold an exact vector although Kotlin reads
                // its elements through the declaration carrier. A store must supply
                // the CLR component of that location, not just its readable view.
                if (value.Elem.OrDefault() is TypeNode.Fqn carrier &&
                    ExprType(Graph(value.Array.OrDefault()), scope, index) is TypeNode.Array { Elem: TypeNode.Fqn exact } &&
                    exact.Args is { Length: > 0 } && index.HasDeclaredCarrier(exact, carrier))
                    value.Elem = exact;
                value.Value = C(value.Value, value.Elem.OrDefault()); break;
            case BirStackSet value: value.Value = C(value.Value, value.Elem.OrDefault()); break;
            case BirByRefStore value:
                var pointee = value.Elem.OrDefault();
                var location = value.Ptr.OrDefault() ?? (value.Local.HasValue ? new BirLocal { Name = value.Local } : null);
                value.Value = Cross(value.Value, pointee, MayHoldArray(pointee, index) && ManagedAddress(location, scope, index) != null);
                break;
            case BirNewArray value: CoerceArray(value.Elems.OrDefault(), value.Elem.OrDefault(), scope, index); break;
            case BirNewList value: CoerceArray(value.Elems.OrDefault(), value.Elem.OrDefault(), scope, index); break;
            case BirConditional value:
                value.Cond = C(value.Cond, new TypeNode.Fqn("System.Boolean"));
                value.Then = C(value.Then, value.Type.OrDefault()); value.Else = C(value.Else, value.Type.OrDefault()); break;
            case BirBranch value: value.Cond = C(value.Cond, new TypeNode.Fqn("System.Boolean")); break;
            case BirWhile value: value.Cond = C(value.Cond, new TypeNode.Fqn("System.Boolean")); break;
        }
        if (Receiver(node) is { } receiver && Member(node) is { } member)
        {
            var target = node switch {
                BirConstrainedCall value => value.RecvType.OrDefault(),
                BirNativeInstance value => value.Type.OrDefault(),
                BirPropertyGet value => value.Type.OrDefault(),
                BirPropertySet value => value.Type.OrDefault(),
                _ => member.DeclaringType.OrDefault(),
            };
            if (target != null) SetReceiver(node, CoerceExpression(receiver, target, scope, index));
        }
    }

    static void CoerceDelegateArguments(BirDelegateInvoke node, Scope scope, Index index)
    {
        if (!node.InvokeRef.HasValue || !node.Args.HasValue)
            throw new InvalidOperationException("bir2cir: delegate invocation has no resolved argument contract");
        var invoke = node.InvokeRef.Value;
        if (node.Recv.HasValue) node.Recv = Field(CoerceExpression(node.Recv.Value, invoke.DeclaringType.OrDefault(), scope, index, node.FuncType.OrDefault()));
        var targets = invoke.ParameterTypes.OrDefault()
            ?? throw new InvalidOperationException("bir2cir: delegate invocation has no parameter types");
        var args = node.Args.Value;
        if (targets.Length != args.Length)
            throw new InvalidOperationException("bir2cir: delegate invocation argument count differs from its resolved contract");
        targets = targets.Select(type => Close(type, OwnerArgs(invoke), [])).ToArray();
        CoerceVector(args, targets, scope, index);
        for (var i = 0; i < args.Length; i++)
            if (args[i] is { } argument && targets[i] is not TypeNode.ByRef
                && targets[i] != ExprType(Graph(argument), scope, index)) args[i] = Cast(Graph(argument), targets[i]);
    }

    static void CoerceConstructorArguments(BirGraphValue node, Scope scope, Index index)
    {
        var member = Member(node);
        var targets = member != null ? ParameterTypes(node) : ArgumentTypes(node);
        var args = Arguments(node);
        CoerceVector(args, targets, scope, index, ArgumentTypes(node));
        if (member?.Kind.OrDefault() != "ctor" || args == null || targets?.Length != args.Length) return;
        for (var i = 0; i < args.Length; i++)
            if (args[i] is { } argument
                && ExprType(Graph(argument), scope, index) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
                && targets[i] is TypeNode.Fqn or TypeNode.Array or TypeNode.Fn or TypeNode.Tv
                && targets[i] is not TypeNode.Fqn { Args: null, Name: "object" or "System.Object" })
                args[i] = Cast(Graph(argument), targets[i]);
    }

    static BirGraphValue CoerceDeclaredResult(BirGraphValue node, Scope scope, Index index, bool resultUsed)
    {
        var declared = DeclaredResult(node);
        if (declared == null) return node;
        var actual = PhysicalResult(node, scope, index);
        var plainCall = node is BirCallStatic or BirCallInstance or BirConstrainedCall;
        var invocation = plainCall || node is BirNativeStatic or BirNativeInstance or BirGenericStatic or BirGenericInstance;
        var objectGeneric = invocation && actual is TypeNode.Tv && declared is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" };
        if (!resultUsed && actual != null && (plainCall || objectGeneric))
        { ReplaceResultStamps(node, actual); return node; }
        if (node is BirFieldGet or BirStaticFieldGet or BirLateinitGet
            && declared is TypeNode.Tv && actual is TypeNode.Tv && declared != actual)
        { ReplaceResultStamps(node, actual); return node; }
        if (IsVoid(declared) && actual != null && !IsVoid(actual))
        { ReplaceResultStamps(node, actual, includeSty: false); return node; }
        // An erased result can be the original Kotlin function object, not a CLR delegate.
        // Leave the call at its physical return type until its function view is obtained.
        if (resultUsed && invocation && Underlying(declared) is TypeNode.Fn { Suspend: false }
            && FunctionViewOperand(node, FunctionSlot(actual, declared, index), declared, index) is { } functionView)
        {
            ReplaceResultStamps(node, actual);
            return Cast(functionView, declared);
        }
        var genericObject = declared is TypeNode.Tv && actual is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" } && plainCall;
        var genericSlots = declared is TypeNode.Tv && actual is TypeNode.Tv && declared != actual && invocation;
        var arrayStorage = SignatureValueTypes.Of(actual) is TypeNode.Fqn { Name: "System.Array" }
            && SignatureValueTypes.Of(declared) is TypeNode.Array;
        var opaqueReference = actual != null && actual != declared
            && (SignatureValueTypes.Of(actual) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
                || SignatureValueTypes.Of(declared) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" })
            && IsReferenceSlot(actual, index.IsValue) && IsReferenceSlot(declared, index.IsValue);
        var opaqueValue = resultUsed && NeedsBox(declared, index.IsValue) && declared is not TypeNode.Tv
            && SignatureValueTypes.Of(actual) is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" }
            && (invocation || node is BirDelegateInvoke);
        if (!genericObject && !objectGeneric && !genericSlots && !arrayStorage && !opaqueReference && !opaqueValue
            && !CollectionViewFaces.IsViewSeam(actual, declared)) return node;
        ReplaceResultStamps(node, actual);
        return Cast(FunctionViewOperand(node, actual, declared, index) ?? node, declared);
    }

    static BirGraphValue LowerIdentity(BirBinaryOperation value, Scope scope, Index index)
    {
        var lhs = value.Lhs.Value; var rhs = value.Rhs.Value;
        var left = ExprType(Graph(lhs), scope, index); var right = ExprType(Graph(rhs), scope, index);
        if (left == null || right == null) throw new InvalidOperationException("bir2cir: identity comparison lacks a physical operand type");
        bool nullLeft = KotlinEqualityLowering.IsNull(lhs), nullRight = KotlinEqualityLowering.IsNull(rhs);
        if ((nullLeft || nullRight) && index.IsStackOnly(nullLeft ? right : left))
        {
            // A stack-only value cannot be null or boxed, but its evaluation can throw or have side effects.
            var operand = nullLeft ? rhs : lhs;
            var flag = new TypeNode.Fqn("System.Boolean");
            var result = new BirConstant { Type = flag, Value = new BirLiteral.BooleanLiteral(false) };
            return operand is BirLocal ? result : new BirValueBlock { Type = flag,
                Stmts = new IBirStatement[] { new BirExpressionStatement { Expr = Field(operand) } }, Result = result };
        }
        if (nullLeft || nullRight || left != right)
        {
            ValidateNativePointerConversion(left, Object); ValidateNativePointerConversion(right, Object);
            if (left != Object) value.Lhs = Cast(FunctionViewOperand(Graph(lhs), left, Object, index) ?? Graph(lhs), Object);
            if (right != Object) value.Rhs = Cast(FunctionViewOperand(Graph(rhs), right, Object, index) ?? Graph(rhs), Object);
        }
        value.Op = "==";
        return value;
    }

    static void WidenNumericOperands(BirBinaryOperation node, Scope scope, Index index)
    {
        if (node.Op.OrDefault() is "<<" or ">>" or ">>>" or "===") return;
        var left = BirTypeLowering.CanonicalExpressionResult(ExprType(Graph(node.Lhs.OrDefault()), scope, index));
        var right = BirTypeLowering.CanonicalExpressionResult(ExprType(Graph(node.Rhs.OrDefault()), scope, index));
        var leftRank = NumericRank(left); var rightRank = NumericRank(right);
        if (leftRank < 0 || rightRank < 0 || left == right) return;
        var target = Math.Max(leftRank, rightRank) < 2 ? new TypeNode.Fqn("System.Int32") : leftRank >= rightRank ? left : right;
        if (left != target && node.Lhs.HasValue) node.Lhs = new BirConvert { To = target, E = node.Lhs };
        if (right != target && node.Rhs.HasValue) node.Rhs = new BirConvert { To = target, E = node.Rhs };
    }

    static TypeNode ExprType(BirGraphValue node, Scope scope, Index index)
    {
        if (node == null) return null;
        if (node is BirLocal { Name.HasValue: true } local && scope.Locals.TryGetValue(local.Name.Value, out var type)) return type;
        if (node is BirThis) return scope.Owner;
        var calleeRet = node switch { BirCallStatic call => call.CalleeRet.OrDefault(),
            BirCallInstance call => call.CalleeRet.OrDefault(), BirConstrainedCall call => call.CalleeRet.OrDefault(), _ => null };
        if (calleeRet != null) return SignatureValueTypes.Of(Close(calleeRet, CallOwner(node)?.Args, MethodArgs(node)));
        if (PhysicalResult(node, scope, index) is { } physical)
        {
            if (node is BirNativeStatic or BirNativeInstance && physical is TypeNode.ByRef reference) physical = reference.Of;
            return SignatureValueTypes.Of(FunctionSlot(physical, DeclaredResult(node), index));
        }
        var inferred = BirNodeType.Of(node as BirGraphNode, child => ExprType(child, scope, index),
            name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null);
        return SignatureValueTypes.Of(index.ReferenceBuild ? inferred : BirTypeLowering.CanonicalExpressionResult(inferred));
    }

    static TypeNode.ByRef ManagedAddress(IBirExpression value, Scope scope, Index index) => value switch {
        BirByRefOf a when a.Inner.OrDefault() is BirGraphValue call && Member(call)?.ReturnType.OrDefault() is TypeNode.ByRef
            => PhysicalResult(call, scope, index) as TypeNode.ByRef,
        BirValueBlock block => ManagedAddress(block.Result.OrDefault(), scope, index),
        BirLocal l when scope.ManagedAddresses.Contains(l.Name.OrDefault()) => ExprType(l, scope, index) as TypeNode.ByRef,
        _ => null,
    };

    static TypeNode PhysicalResult(BirGraphValue node, Scope scope, Index index)
    {
        if (node is BirCast cast) return cast.Type.OrDefault();
        if (Member(node) is { } member
            && node is BirCallStatic or BirCallInstance or BirConstrainedCall or BirNativeStatic or BirNativeInstance
                or BirGenericStatic or BirGenericInstance or BirPropertyGet or BirFieldGet or BirStaticFieldGet or BirClrStaticField or BirLateinitGet)
            return Close(member.ReturnType.OrDefault(), OwnerArgs(member), MethodArgs(node));
        if (node is BirCallStatic or BirCallInstance or BirConstrainedCall && index.Method(node) is { } method)
            return Close(method.Return, CallOwner(node)?.Args, MethodArgs(node));
        if (node is BirFieldGet or BirStaticFieldGet or BirLateinitGet)
        {
            var owner = FieldOwner(node, scope, index);
            return Close(index.Field(owner?.Name, FieldName(node)), owner?.Args, MethodArgs(node));
        }
        return null;
    }

    static TypeNode FieldTarget(BirGraphValue node, Scope scope, Index index)
    {
        if (Member(node) is { } member)
            return Close(member.Kind.OrDefault() == "field" ? member.ReturnType.OrDefault()
                : member.ParameterTypes.OrDefault()?.LastOrDefault(), OwnerArgs(member), MethodArgs(node));
        var owner = FieldOwner(node, scope, index);
        var declared = node switch { BirSetField field => field.MemberType.OrDefault(),
            BirFieldSetExpression field => field.MemberType.OrDefault(), BirStaticFieldSet field => field.MemberType.OrDefault(), _ => null };
        return Close(declared ?? index.Field(owner?.Name, FieldName(node)), owner?.Args, MethodArgs(node));
    }

    static TypeNode.Fqn FieldOwner(BirGraphValue node, Scope scope, Index index) => CallOwner(node)
        ?? ExprType(Graph(Receiver(node)), scope, index) as TypeNode.Fqn;
    static TypeNode[] ConstructorParameterTypes(BirConstructor constructor) => constructor.BaseCtorRef.HasValue
        ? constructor.BaseCtorRef.Value.ParameterTypes.OrDefault()?.Select(type => Close(type, OwnerArgs(constructor.BaseCtorRef.Value), [])).ToArray()
        : constructor.DelegationSig.OrDefault();

    static BirMemberReference Member(BirGraphValue node) => node switch {
        BirCallStatic value => value.MemberRef.OrDefault(), BirCallInstance value => value.MemberRef.OrDefault(),
        BirConstrainedCall value => value.MemberRef.OrDefault(), BirNativeStatic value => value.MemberRef.OrDefault(),
        BirNativeInstance value => value.MemberRef.OrDefault(), BirGenericStatic value => value.MemberRef.OrDefault(),
        BirGenericInstance value => value.MemberRef.OrDefault(), BirNew value => value.MemberRef.OrDefault(),
        BirNewClr value => value.MemberRef.OrDefault(), BirFieldGet value => value.MemberRef.OrDefault(),
        BirSetField value => value.MemberRef.OrDefault(), BirFieldSetExpression value => value.MemberRef.OrDefault(),
        BirStaticFieldGet value => value.FieldRef.OrDefault(), BirStaticFieldSet value => value.FieldRef.OrDefault(),
        BirLateinitGet value => value.FieldRef.OrDefault(), BirClrStaticField value => value.FieldRef.OrDefault(),
        BirPropertyGet value => value.MemberRef.OrDefault(), BirPropertySet value => value.MemberRef.OrDefault(),
        BirEventAdd value => value.MemberRef.OrDefault(), BirEventRemove value => value.MemberRef.OrDefault(),
        BirNewBoundDelegate value => value.MemberRef.OrDefault(), BirBoundClrDelegate value => value.MemberRef.OrDefault(),
        _ => null,
    };
    static IBirExpression[] Arguments(BirGraphValue node) => node switch {
        BirCallStatic value => value.Args.OrDefault(), BirCallInstance value => value.Args.OrDefault(),
        BirConstrainedCall value => value.Args.OrDefault(), BirNativeStatic value => value.Args.OrDefault(),
        BirNativeInstance value => value.Args.OrDefault(), BirGenericStatic value => value.Args.OrDefault(),
        BirGenericInstance value => value.Args.OrDefault(), BirNew value => value.Args.OrDefault(),
        BirNewClr value => value.Args.OrDefault(), _ => null,
    };
    static TypeNode[] MethodArgs(BirGraphValue node) => (node switch {
        BirCallStatic value => value.TypeArgs.OrDefault(), BirCallInstance value => value.TypeArgs.OrDefault(),
        BirConstrainedCall value => value.TypeArgs.OrDefault(), BirNativeStatic value => value.TypeArgs.OrDefault(),
        BirNativeInstance value => value.TypeArgs.OrDefault(), BirGenericStatic value => value.TypeArgs.OrDefault(),
        BirGenericInstance value => value.TypeArgs.OrDefault(), BirNewClosure value => value.TypeArgs.OrDefault(), _ => null,
    }) ?? [];
    static TypeNode[] Signature(BirGraphValue node) => node switch {
        BirCallStatic value => value.Sig.OrDefault(), BirCallInstance value => value.Sig.OrDefault(),
        BirConstrainedCall value => value.Sig.OrDefault(), BirNativeStatic value => value.Sig.OrDefault(),
        BirNativeInstance value => value.Sig.OrDefault(), BirFieldGet value => value.Sig.OrDefault(),
        BirSetField value => value.Sig.OrDefault(), BirFieldSetExpression value => value.Sig.OrDefault(),
        BirPropertyGet value => value.Sig.OrDefault(), BirPropertySet value => value.Sig.OrDefault(), _ => null,
    };
    static TypeNode[] ArgumentTypes(BirGraphValue node) => node switch {
        BirCallStatic value => value.ArgTypes.OrDefault(), BirCallInstance value => value.ArgTypes.OrDefault(),
        BirConstrainedCall value => value.ArgTypes.OrDefault(), BirNativeStatic value => value.ArgTypes.OrDefault(),
        BirNativeInstance value => value.ArgTypes.OrDefault(), BirNew value => value.ArgTypes.OrDefault(),
        BirNewClr value => value.ArgTypes.OrDefault(), _ => null,
    };
    // Reflection spells the same delegate as a constructed named type. Keep
    // the selected Kotlin function slot for value conversion without changing
    // the exact referenced method signature used for CLR linkage.
    static TypeNode FunctionSlot(TypeNode physical, TypeNode selected, Index index) =>
        Underlying(selected) is TypeNode.Fn { Suspend: false } && Underlying(physical) is TypeNode.Fqn named
            && index?.References?.IsClrDelegate(named) == true ? selected : physical;

    static TypeNode[] ParameterTypes(BirGraphValue node)
    {
        var member = Member(node);
        var parameters = member?.ParameterTypes.OrDefault();
        parameters ??= node switch {
            BirConstrainedCall value => value.ResolvedMemberParams.OrDefault(),
            BirNativeStatic value => value.ResolvedMemberParams.OrDefault(), BirNativeInstance value => value.ResolvedMemberParams.OrDefault(),
            BirGenericStatic value => value.ResolvedMemberParams.OrDefault(), BirGenericInstance value => value.ResolvedMemberParams.OrDefault(), _ => null,
        };
        parameters ??= Signature(node) ?? (node switch {
            BirCallStatic value => value.ShapeTypes.OrDefault(), BirCallInstance value => value.ShapeTypes.OrDefault(), _ => null,
        }) ?? ArgumentTypes(node);
        if (parameters == null) return null;
        var ownerArgs = member != null ? OwnerArgs(member) : CallOwner(node)?.Args;
        return parameters.Select(type => Close(type, ownerArgs, MethodArgs(node))).ToArray();
    }
    static string MethodName(BirGraphValue node) => node switch {
        BirCallStatic value => value.Method.OrDefault(), BirCallInstance value => value.Method.OrDefault(),
        BirConstrainedCall value => value.Method.OrDefault(), _ => null,
    };
    static string FieldName(BirGraphValue node) => node switch {
        BirFieldGet value => value.Name.OrDefault(), BirSetField value => value.Name.OrDefault(),
        BirStaticFieldGet value => value.Name.OrDefault(), BirStaticFieldSet value => value.Name.OrDefault(),
        BirFieldSetExpression value => value.Name.OrDefault(), BirLateinitGet value => value.Name.OrDefault(), _ => null,
    };
    static TypeNode.Fqn CallOwner(BirGraphValue node) => (node switch {
        BirCallStatic value => value.OwnerType.OrDefault() ?? value.Owner.OrDefault() ?? value.CalleeOwner.OrDefault(),
        BirCallInstance value => value.OwnerType.OrDefault(), BirConstrainedCall value => value.Iface.OrDefault(),
        BirNativeStatic value => value.Type.OrDefault(), BirNativeInstance value => value.Type.OrDefault(),
        BirGenericStatic value => value.Type.OrDefault(), BirGenericInstance value => value.Type.OrDefault(),
        BirFieldGet value => value.OwnerType.OrDefault() ?? value.Type.OrDefault(), BirStaticFieldGet value => value.OwnerType.OrDefault(),
        BirSetField value => value.OwnerType.OrDefault() ?? value.Type.OrDefault(), BirStaticFieldSet value => value.OwnerType.OrDefault(),
        BirFieldSetExpression value => value.OwnerType.OrDefault() ?? value.Type.OrDefault(), BirLateinitGet value => value.OwnerType.OrDefault(),
        BirNew value => value.Type.OrDefault(), BirNewClr value => value.Type.OrDefault(),
        BirPropertyGet value => value.Type.OrDefault(), BirPropertySet value => value.Type.OrDefault(), _ => null,
    }) as TypeNode.Fqn;
    static IBirExpression Receiver(BirGraphValue node) => node switch {
        BirCallInstance value => value.Recv.OrDefault(), BirConstrainedCall value => value.Recv.OrDefault(),
        BirNativeStatic value => value.Recv.OrDefault(), BirNativeInstance value => value.Recv.OrDefault(),
        BirGenericInstance value => value.Recv.OrDefault(), BirFieldGet value => value.Recv.OrDefault(),
        BirSetField value => value.Recv.OrDefault(), BirFieldSetExpression value => value.Recv.OrDefault(),
        BirLateinitGet value => value.Recv.OrDefault(), BirPropertyGet value => value.Recv.OrDefault(),
        BirPropertySet value => value.Recv.OrDefault(), BirEventAdd value => value.Recv.OrDefault(),
        BirEventRemove value => value.Recv.OrDefault(), BirNewBoundDelegate value => value.Recv.OrDefault(),
        BirBoundClrDelegate value => value.Recv.OrDefault(), _ => null,
    };
    static void SetReceiver(BirGraphValue node, IBirExpression receiver)
    {
        var field = Field(receiver);
        switch (node)
        {
            case BirCallInstance value: value.Recv = field; break;
            case BirConstrainedCall value: value.Recv = field; break;
            case BirNativeStatic value: value.Recv = field; break;
            case BirNativeInstance value: value.Recv = field; break;
            case BirGenericInstance value: value.Recv = field; break;
            case BirFieldGet value: value.Recv = field; break;
            case BirSetField value: value.Recv = field; break;
            case BirFieldSetExpression value: value.Recv = field; break;
            case BirLateinitGet value: value.Recv = field; break;
            case BirPropertyGet value: value.Recv = field; break;
            case BirPropertySet value: value.Recv = field; break;
            case BirEventAdd value: value.Recv = field; break;
            case BirEventRemove value: value.Recv = field; break;
            case BirNewBoundDelegate value: value.Recv = field; break;
            case BirBoundClrDelegate value: value.Recv = field; break;
            default: throw new InvalidOperationException("Unaccounted resolved receiver " + node.GetType().Name);
        }
    }
    static TypeNode DeclaredResult(BirGraphValue node) => node switch {
        BirCallStatic value => value.Ret.OrDefault() ?? value.DynRet.OrDefault(),
        BirCallInstance value => value.Ret.OrDefault() ?? value.DynRet.OrDefault(),
        BirConstrainedCall value => value.Ret.OrDefault(), BirNativeStatic value => value.Ret.OrDefault(),
        BirNativeInstance value => value.Ret.OrDefault(), BirGenericStatic value => value.Ret.OrDefault(),
        BirGenericInstance value => value.Ret.OrDefault(), BirFieldGet value => value.Ret.OrDefault(),
        BirStaticFieldGet value => value.Ret.OrDefault(), BirPropertyGet value => value.Ret.OrDefault(),
        BirFieldSetExpression value => value.Ret.OrDefault(), BirCallLocal value => value.Ret.OrDefault(), _ => null,
    };
    static void ReplaceResultStamps(BirGraphValue node, TypeNode type, bool includeSty = true)
    {
        OptionalField<TypeNode> R(OptionalField<TypeNode> old) => old.HasValue ? OptionalField<TypeNode>.Of(type) : old;
        OptionalField<TypeNode> S(OptionalField<TypeNode> old) => includeSty ? R(old) : old;
        switch (node)
        {
            case BirCallStatic value: value.Ret = R(value.Ret); value.DynRet = R(value.DynRet); value.Sty = S(value.Sty); break;
            case BirCallInstance value: value.Ret = R(value.Ret); value.DynRet = R(value.DynRet); value.Sty = S(value.Sty); break;
            case BirConstrainedCall value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirNativeStatic value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirNativeInstance value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirGenericStatic value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirGenericInstance value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirFieldGet value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirStaticFieldGet value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirPropertyGet value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            case BirFieldSetExpression value: value.Ret = R(value.Ret); break;
            case BirCallLocal value: value.Ret = R(value.Ret); value.Sty = S(value.Sty); break;
            default: throw new InvalidOperationException("Unaccounted physical result " + node.GetType().Name);
        }
    }

    internal static void SelfTest()
    {
        var integer = new TypeNode.Fqn("System.Int32");
        var text = new TypeNode.Fqn("System.String");
        var target = new TypeNode.Fqn("System.Collections.Generic.IReadOnlyList", new TypeNode[] { text });
        var source = new TypeNode.Fqn("System.Collections.Generic.IList", new TypeNode[] { text });
        var value = new BirLocal { Name = "items" };
        var branch = new BirConditional { Cond = new BirConstant { Type = new TypeNode.Fqn("System.Boolean"),
            Value = new BirLiteral.BooleanLiteral(true) }, Then = value,
            Else = new BirLocal { Name = "items" } };
        var view = new BirFunctionDeclaration { Name = "view", Params = new[] { new BirParameter { Name = "items", Type = source } },
            Ret = target, Body = new IBirStatement[] { new BirReturn { Value = branch } } };
        var physical = new BirNativeStatic { Type = new TypeNode.Fqn("Probe.Native"), Method = "Read", Args = Array.Empty<IBirExpression>(),
            Ret = integer, MemberRef = new BirMemberReference { Kind = "method", DeclaringType = new TypeNode.Fqn("Probe.Native"),
                ReturnType = Object, ParameterTypes = Array.Empty<TypeNode>() } };
        var read = new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Ret = integer,
            Body = new IBirStatement[] { new BirReturn { Value = physical } } };
        var mutation = new BirNativeStatic { Type = new TypeNode.Fqn("Probe.Native"), Method = "Mutate",
            MemberRef = new BirMemberReference { Kind = "method", DeclaringType = new TypeNode.Fqn("Probe.Native"),
                ReturnType = Void, ParameterTypes = new TypeNode[] { new TypeNode.ByRef(text) } },
            Args = new IBirExpression[] { new BirByRefOf { Inner = new BirLocal { Name = "slot" } } }, Ret = Void };
        var native = new BirFunctionDeclaration { Name = "native", Params = new[] { new BirParameter { Name = "slot", Type = Object } },
            Ret = Void, Body = new IBirStatement[] { new BirExpressionStatement { Expr = mutation } } };
        var delegateReceivers = new List<(BirGraphValue Delegate, BirLocal Original, TypeNode Target)>();
        var methods = new List<BirFunctionDeclaration> { view, read, native };
        foreach (var nativeDelegate in new[] { false, true })
        foreach (var (receiverType, declaringType) in new[] {
            (Object, (TypeNode)new TypeNode.Fqn("Probe.Receiver")), (source, (TypeNode)target),
        })
        {
            var receiver = new BirLocal { Name = "receiver" };
            var member = new BirMemberReference { Kind = "method", Assembly = "Probe",
                DeclaringType = declaringType, Name = "Target", GenericArity = 0,
                CallingConvention = "instance", ParameterTypes = Array.Empty<TypeNode>(), ReturnType = Void };
            var functionType = new TypeNode.Fn(false, Void, Array.Empty<TypeNode>());
            IBirExpression bound = nativeDelegate
                ? new BirBoundClrDelegate { Type = declaringType, ClrType = new TypeNode.Fqn("System.Action"),
                    FuncType = functionType, Recv = receiver, MemberRef = member, Method = "Target" }
                : new BirNewBoundDelegate { OwnerType = declaringType, FuncType = functionType,
                    Recv = receiver, MemberRef = member, Method = "Target" };
            methods.Add(new BirFunctionDeclaration { Name = "boundReceiver" + methods.Count,
                Params = new[] { new BirParameter { Name = "receiver", Type = receiverType } }, Ret = Void,
                Body = new IBirStatement[] { new BirExpressionStatement { Expr = Field(bound) } } });
            delegateReceivers.Add((Graph(bound), receiver, declaringType));
        }
        var root = new BirFileDeclaration { FileClass = "PhysicalValues", Methods = methods.ToArray() };
        ApplyAll(null, new PhysicalDelegateDeclarations(), null, new[] { root }, () => new BirConstant { Type = new TypeNode.Fqn("kotlin.Unit"), Value = new BirLiteral.NullLiteral() }, _ => false);
        if (branch.Type.OrDefault() != target || branch.Then.OrDefault() is not BirCast { Type.HasValue: true } first
            || first.Type.Value != target || !ReferenceEquals(first.E.OrDefault(), value)
            || branch.Else.OrDefault() is not BirCast)
            throw new InvalidOperationException("Owning collection view coercion lost a branch edge or its occurrence");
        if (read.Body.Value[0] is not BirReturn { Value.HasValue: true } returned
            || returned.Value.Value is not BirCast { Type.HasValue: true, E.HasValue: true } extraction
            || extraction.Type.Value != integer || !ReferenceEquals(extraction.E.Value, physical) || physical.Ret.OrDefault() != Object)
            throw new InvalidOperationException("Owning exact result projection lost the physical inner call");
        if (native.Body.Value[0] is not BirVariable { Type.HasValue: true } slot || slot.Type.Value != text
            || native.Params.Value[0].Type.Value != Object
            || mutation.Args.Value[0] is not BirByRefOf { Inner.HasValue: true } address
            || address.Inner.Value is not BirLocal local || local.Name.OrDefault() != slot.Name.OrDefault())
            throw new InvalidOperationException("Owning managed-reference argument lost its exact shared storage");
        var open = new TypeNode.Tv("type", 0);
        var call = new BirNativeStatic { Type = new TypeNode.Fqn("Probe.Owner", new TypeNode[] { text }),
            MemberRef = new BirMemberReference { DeclaringType = new TypeNode.Fqn("Probe.Owner", new TypeNode[] { text }),
                ReturnType = new TypeNode.ByRef(open), ParameterTypes = Array.Empty<TypeNode>() } };
        var oracle = ResultTypeOracle(new[] { root });
        var erasedAddress = new BirByRefOf { Inner = new BirLocal { Name = "erased", Sty = new TypeNode.Nullable(integer) } };
        if (oracle(erasedAddress, new Dictionary<string, TypeNode> { ["erased"] = Object }) != new TypeNode.ByRef(Object))
            throw new InvalidOperationException("Pointer type ignored its location's actual physical storage frame");
        if (oracle(call, new Dictionary<string, TypeNode>()) != text)
            throw new InvalidOperationException("Owning physical result oracle did not close and copy the exact ref return");
        foreach (var (bound, receiver, declaringType) in delegateReceivers)
            if (Receiver(bound) is not BirCast { Type.HasValue: true, E.HasValue: true } conversion
                || conversion.Type.Value != declaringType || !ReferenceEquals(conversion.E.Value, receiver))
                throw new InvalidOperationException("Owning bound delegate lost its exact declaring receiver conversion: "
                    + bound.GetType().Name + " -> " + declaringType);
        var genericSlot = new TypeNode.Tv("method", 0);
        foreach (var erased in new TypeNode[] { Object, source, text, new TypeNode.Array(text) })
        {
            var input = new BirLocal { Name = "input" };
            var storage = new BirVariable { Name = "stored", Type = genericSlot, Init = input };
            var returnedBound = new BirReturn { Value = new BirLocal { Name = "input" } };
            var method = new BirFunctionDeclaration { Name = "storeBound", Ret = genericSlot,
                Params = new[] { new BirParameter { Name = "input", Type = erased } },
                Body = new IBirStatement[] { storage, returnedBound } };
            ApplyAll(null, new PhysicalDelegateDeclarations(), null, new[] { new BirFileDeclaration { Methods = new[] { method } } },
                () => throw new InvalidOperationException("Unexpected Unit conversion"), _ => false);
            if (storage.Init.Value is not BirCast { Type.HasValue: true, E.HasValue: true } stored
                || stored.Type.Value != genericSlot || !ReferenceEquals(stored.E.Value, input)
                || returnedBound.Value.Value is not BirCast { Type.HasValue: true } result
                || result.Type.Value != genericSlot)
                throw new InvalidOperationException("An erased bound was not narrowed at its actual generic storage/return edge");
        }
        var baseType = new TypeNode.Fqn("PhysicalBound");
        var derivedType = new TypeNode.Fqn("PhysicalDerived");
        var narrowed = new BirVariable { Name = "narrowed", Type = derivedType, Init = new BirLocal { Name = "input" } };
        var consumeDerived = new BirNativeStatic { Args = new IBirExpression[] { new BirLocal { Name = "input" } },
            MemberRef = new BirMemberReference { ParameterTypes = new TypeNode[] { derivedType }, ReturnType = Void } };
        var returnDerived = new BirReturn { Value = new BirLocal { Name = "input" } };
        var returnBase = new BirReturn { Value = new BirLocal { Name = "input" } };
        var references = new BirFileDeclaration { Types = new IBirTypeDeclaration[] {
                new BirClassDefinition { Name = "PhysicalBound", Kind = "class" },
                new BirClassDefinition { Name = "PhysicalDerived", Kind = "class", Base = baseType } },
            Methods = new[] {
                new BirFunctionDeclaration { Name = "narrow", Ret = derivedType,
                    Params = new[] { new BirParameter { Name = "input", Type = baseType } },
                    Body = new IBirStatement[] { narrowed, new BirExpressionStatement { Expr = consumeDerived }, returnDerived } },
                new BirFunctionDeclaration { Name = "widen", Ret = baseType,
                    Params = new[] { new BirParameter { Name = "input", Type = derivedType } },
                    Body = new IBirStatement[] { returnBase } } } };
        ApplyAll(null, new PhysicalDelegateDeclarations(), null, new[] { references }, () => throw new InvalidOperationException("Unexpected Unit conversion"), _ => false);
        if (narrowed.Init.Value is not BirCast { Type.HasValue: true } narrowedValue || narrowedValue.Type.Value != derivedType
            || consumeDerived.Args.Value[0] is not BirCast { Type.HasValue: true } argument || argument.Type.Value != derivedType
            || returnDerived.Value.Value is not BirCast { Type.HasValue: true } returnedDerived || returnedDerived.Type.Value != derivedType
            || returnBase.Value.Value is not BirLocal)
            throw new InvalidOperationException("Physical base-to-derived consumers lost narrowing or acquired an upcast check");
        Console.WriteLine("[typed physical values] self-test OK (conditional views, exact results, native storage, closed ref results, bound receivers)");
    }
}
