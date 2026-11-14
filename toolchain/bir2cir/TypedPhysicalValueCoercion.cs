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

    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots, Func<IBirExpression> unitValue,
        ValueTypeOracle isValue, bool referenceBuild = false, Func<TypeNode.Fqn, bool> isReferenceOwner = null,
        Func<TypeNode.Fqn, bool> isExistentialOwner = null, Func<TypeNode.Fqn, bool> isNativeReference = null,
        Func<TypeNode.Fqn, TypeNode.Fqn, bool> referencedCarrier = null, ReferenceMetadataIndex references = null)
    {
        var index = new Index(roots, unitValue) {
            IsValue = isValue, ReferenceBuild = referenceBuild,
            IsReferenceOwner = isReferenceOwner ?? (_ => false),
            IsExistentialOwner = isExistentialOwner ?? (_ => false),
            IsNativeReference = isNativeReference ?? (_ => false),
            ReferencedCarrier = referencedCarrier, References = references,
            DelegateAdapters = new TypedPhysicalDelegateAdapter(references, unitValue),
        };
        foreach (var root in roots)
        {
            index.Document = root;
            index.ArrayNames = new TypedGenericArrayStorageLowering.FreshNames(root);
            RewriteMembers(root.Fields.OrEmpty(), [], root.Methods.OrEmpty(),
                root.FileClass.HasValue ? new TypeNode.Fqn(root.FileClass.Value) : null, index);
            // Synthesis can append declarations while rewriting a member.
            foreach (var type in root.Types.OrEmpty().ToArray()) RewriteType(type, index);
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
        readonly Dictionary<string, SupertypeGraph.Def> types;
        internal readonly Func<IBirExpression> UnitValue;
        internal ValueTypeOracle IsValue = _ => false;
        internal Func<TypeNode.Fqn, bool> IsReferenceOwner = _ => false;
        internal Func<TypeNode.Fqn, bool> IsExistentialOwner = _ => false;
        internal Func<TypeNode.Fqn, bool> IsNativeReference = _ => false;
        internal Func<TypeNode.Fqn, TypeNode.Fqn, bool> ReferencedCarrier;
        internal bool ReferenceBuild;
        internal ReferenceMetadataIndex References;
        internal BirFileDeclaration Document;
        internal TypedGenericArrayStorageLowering.FreshNames ArrayNames;
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
        internal bool HasDeclaredCarrier(TypeNode.Fqn target, TypeNode.Fqn source) =>
            declaredCarriers.Contains((target.Name, source.Name)) || ReferencedCarrier?.Invoke(target, source) == true
            || declaredCarriers.Any(edge => edge.Class == target.Name
                && SupertypeGraph.Reaches(source, new TypeNode.Fqn(edge.Carrier), types, References))
            || References?.TryExistentialPhysicalOwner(target.Name, out var carrier) == true
                && SupertypeGraph.Reaches(source, new TypeNode.Fqn(carrier), types, References);
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
        internal readonly Dictionary<string, TypeNode> TemporaryFields, TemporaryLocals;
        internal Scope(TypeNode owner = null, TypeNode ret = null, Dictionary<string, TypeNode> locals = null,
            Dictionary<string, TypeNode> temporaryFields = null, Dictionary<string, TypeNode> temporaryLocals = null)
        {
            Owner = owner; Return = ret;
            Locals = locals ?? new(StringComparer.Ordinal);
            TemporaryFields = temporaryFields ?? new(StringComparer.Ordinal);
            TemporaryLocals = temporaryLocals ?? new(StringComparer.Ordinal);
        }
        internal Scope Copy() => new(Owner, Return, new(Locals, StringComparer.Ordinal), TemporaryFields, TemporaryLocals);
        internal static Scope Frame(BirParameter[] parameters, IEnumerable<BirGraphValue> parts, TypeNode owner, TypeNode ret)
        {
            var scope = new Scope(owner, ret);
            foreach (var parameter in parameters)
                if (parameter.Name.HasValue && parameter.Type.HasValue)
                    scope.Locals[parameter.Name.Value] = SignatureValueTypes.Of(parameter.Type.Value);
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
            if (!index.ReferenceBuild) CoerceVector(arguments, ConstructorParameterTypes(constructor), scope, index);
            RewriteStatements(constructor.Body.OrEmpty(), scope, index);
        }
        foreach (var method in methods)
        {
            var scope = Scope.Frame(method.Params.OrEmpty(), method.Body.OrEmpty().Cast<BirGraphValue>(), owner, method.Ret.OrDefault());
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
    static IBirExpression CoerceExpression(IBirExpression value, TypeNode target, Scope scope, Index index)
        => value == null ? null : Expression(Coerce(Graph(value), target, scope, index));
    static void CoerceVector(IBirExpression[] values, TypeNode[] targets, Scope scope, Index index)
    {
        if (values == null || targets == null || values.Length != targets.Length) return;
        for (var i = 0; i < values.Length; i++) values[i] = CoerceExpression(values[i], targets[i], scope, index);
    }
    static void CoerceArray(IBirExpression[] values, TypeNode target, Scope scope, Index index)
    {
        if (values == null || target == null) return;
        for (var i = 0; i < values.Length; i++) values[i] = CoerceExpression(values[i], target, scope, index);
    }
    static bool NeedsConversion(TypeNode source, TypeNode target, Index index) =>
        PhysicalValueCoercion.NeedsConversion(source, target, index.IsValue,
            index.IsExistentialOwner, index.IsNativeReference, index.HasDeclaredCarrier);

    static BirGraphValue Coerce(BirGraphValue value, TypeNode target, Scope scope, Index index)
    {
        target = SignatureValueTypes.Of(target);
        if (target != null && value is BirConditional { Type.HasValue: false } conditional
            && (NeedsConversion(ExprType(Graph(conditional.Then.OrDefault()), scope, index), target, index)
                || NeedsConversion(ExprType(Graph(conditional.Else.OrDefault()), scope, index), target, index)))
        {
            if (conditional.Then.HasValue) conditional.Then = Field(CoerceExpression(conditional.Then.Value, target, scope, index));
            if (conditional.Else.HasValue) conditional.Else = Field(CoerceExpression(conditional.Else.Value, target, scope, index));
            conditional.Type = target;
            return conditional;
        }
        var got = ExprType(value, scope, index);
        if (value is IBirExpression expression
            && index.DelegateAdapters?.AdaptUnitDelegateValue(index.Document, expression, got, target) is { } adapter)
            return Graph(adapter);
        return NeedsConversion(got, target, index) ? Cast(value, target) : value;
    }

    internal static void AllocateNativeLocals(IEnumerable<BirGraphValue> body, BirParameter[] parameters,
        List<IBirStatement> prologue, TypedGenericArrayStorageLowering.FreshNames arrayNames = null,
        bool genericParametersOnly = false)
    {
        var required = new Dictionary<BirGraphValue, TypeNode>(ReferenceEqualityComparer.Instance);
        var uses = new Dictionary<BirGraphValue, List<BirGraphValue>>(ReferenceEqualityComparer.Instance);
        var parameterSet = new HashSet<BirGraphValue>(parameters, ReferenceEqualityComparer.Instance);
        var names = parameters.Select(parameter => parameter.Name.OrDefault()).ToHashSet(StringComparer.Ordinal);
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
            if (node is BirBlock or BirValueBlock or BirConditional or BirTry or BirForRange or BirForArray or BirForIn)
                declarations = new(declarations, StringComparer.Ordinal);
            if (node is BirCallStatic or BirCallInstance or BirConstrainedCall or BirNativeStatic or BirNativeInstance
                    or BirGenericStatic or BirGenericInstance or BirNew or BirNewClr
                && Arguments(node) is { } arguments && ParameterTypes(node) is { } callParameters
                && callParameters.Length == arguments.Length)
                for (var i = 0; i < callParameters.Length; i++)
                {
                    if (SignatureValueTypes.Of(callParameters[i]) is not TypeNode.ByRef reference) continue;
                    var location = arguments[i] is BirByRefOf address ? address.Inner.OrDefault() : arguments[i];
                    if (location is not BirLocal { Name.HasValue: true } local
                        || !declarations.TryGetValue(local.Name.Value, out var declaration)
                        || !PhysicalValueCoercion.NativeLocalEligible(genericParametersOnly,
                            parameterSet.Contains(declaration), reference.Of)
                        || SignatureValueTypes.Of(StorageType(declaration)) is TypeNode.ByRef) continue;
                    var element = SignatureValueTypes.Of(reference.Of);
                    if (required.TryGetValue(declaration, out var previous) && previous != element)
                        throw new InvalidOperationException($"bir2cir: local '{local.Name.Value}' is required by distinct CLR managed-reference element types");
                    required[declaration] = element;
                }
            node.VisitChildren(child => Visit(child, declarations));
            if (node is BirVariable { Name.HasValue: true } variable) declarations[variable.Name.Value] = variable;
        }
        var declarations = new Dictionary<string, BirGraphValue>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
            if (parameter.Name.HasValue) declarations[parameter.Name.Value] = parameter;
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
                Init = new BirLocal { Name = ((BirParameter)declaration).Name } });
        }
        foreach (var statement in prefix) arrayNames?.Reserve((BirGraphValue)statement);
        prologue.InsertRange(0, prefix);
    }

    static TypeNode StorageType(BirGraphValue declaration) => declaration switch {
        BirVariable variable => variable.Type.OrDefault(), BirParameter parameter => parameter.Type.OrDefault(), _ => null,
    };

    static BirGraphValue Rewrite(BirGraphValue node, Scope scope, Index index, bool resultUsed = true)
    {
        if (node == null) return null;
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
        RewriteChildren(node, scope, index, resultUsed);

        var array = node switch { BirArrayGet value => value.Array.OrDefault(), BirArraySet value => value.Array.OrDefault(),
            BirArrayLength value => value.Array.OrDefault(), BirForArray value => value.Array.OrDefault(), _ => null };
        if (index.References != null && array != null)
        {
            var rewritten = TypedGenericArrayStorageLowering.LowerOperation(node, index.References,
                index.ArrayNames, ExprType(Graph(array), scope, index));
            if (!ReferenceEquals(rewritten, node)) return Rewrite(rewritten, scope, index, resultUsed);
        }
        if (node is BirByRefLoad load)
        {
            var pointer = load.Ptr.OrDefault() ?? (load.Local.HasValue ? new BirLocal { Name = load.Local.Value } : null);
            if (ExprType(Graph(pointer), scope, index) is TypeNode.ByRef reference) load.Elem = reference.Of;
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
            return new BirValueBlock { Type = new TypeNode.Fqn("kotlin.Unit"),
                Stmts = new IBirStatement[] { new BirExpressionStatement { Expr = Field(Expression(result)) } }, Result = Field(index.UnitValue()) };
        return result;
    }

    static void RewriteChildren(BirGraphValue node, Scope scope, Index index, bool resultUsed)
    {
        OptionalField<IBirExpression> Expr(IBirExpression value, bool used = true) => Field(value == null ? null : Expression(Rewrite(Graph(value), scope, index, used)));
        switch (node)
        {
            case BirExpressionStatement statement:
                if (statement.Expr.HasValue) statement.Expr = Expr(statement.Expr.Value, false); return;
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
                node.VisitChildSlots(slot => slot.Replace(Rewrite(slot.Value, scope, index)));
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
        OptionalField<IBirExpression> C(OptionalField<IBirExpression> value, TypeNode target) => value.HasValue && target != null
            ? OptionalField<IBirExpression>.Of(CoerceExpression(value.Value, target, scope, index)) : value;
        switch (node)
        {
            case BirObjectMethod value:
                ValidateNativePointerConversion(ExprType(Graph(value.Recv.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.Arg.OrDefault()), scope, index), Object); break;
            case BirObjectEquals value:
                ValidateNativePointerConversion(ExprType(Graph(value.Lhs.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.Rhs.OrDefault()), scope, index), Object); break;
            case BirIsInstance value:
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault()); break;
            case BirInstanceOrNull value:
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), Object);
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault()); break;
            case BirConcat value:
                foreach (var part in value.Parts.OrEmpty()) ValidateNativePointerConversion(ExprType(Graph(part), scope, index), Object); break;
            case BirCast value:
                ValidateNativePointerConversion(ExprType(Graph(value.E.OrDefault()), scope, index), value.Type.OrDefault()); break;
            case BirBinaryOperation { Op.HasValue: true, Op.Value: "===" } value:
                var left = ExprType(Graph(value.Lhs.OrDefault()), scope, index);
                var right = ExprType(Graph(value.Rhs.OrDefault()), scope, index);
                if (left == null || right == null) throw new InvalidOperationException("bir2cir: identity comparison lacks a physical operand type");
                if (left != right)
                {
                    ValidateNativePointerConversion(left, Object); ValidateNativePointerConversion(right, Object);
                    value.Lhs = Cast(Graph(value.Lhs.Value), Object); value.Rhs = Cast(Graph(value.Rhs.Value), Object);
                }
                value.Op = "=="; break;
            case BirVariable value: value.Init = C(value.Init, value.Type.OrDefault()); break;
            case BirSetLocal value:
                if (value.Name.HasValue && scope.Locals.TryGetValue(value.Name.Value, out var type)) value.Value = C(value.Value, type); break;
            case BirSetField value: value.Value = C(value.Value, FieldTarget(value, scope, index)); break;
            case BirFieldSetExpression value: value.Value = C(value.Value, FieldTarget(value, scope, index)); break;
            case BirStaticFieldSet value: value.Value = C(value.Value, FieldTarget(value, scope, index)); break;
            case BirReturn value: value.Value = C(value.Value, scope.Return); break;
            case BirReturnExpression value: value.Value = C(value.Value, scope.Return); break;
            case BirValueBlock value: value.Result = C(value.Result, value.Type.OrDefault()); break;
            case BirCallStatic or BirCallInstance or BirConstrainedCall or BirNativeStatic or BirNativeInstance or BirGenericStatic or BirGenericInstance:
                CoerceVector(Arguments(node), ParameterTypes(node), scope, index); break;
            case BirNew or BirNewClr: CoerceConstructorArguments(node, scope, index); break;
            case BirNewClosure value: CoerceVector(value.Captures.OrDefault(), index.ClosureParameters(value), scope, index); break;
            case BirDelegateInvoke value: CoerceDelegateArguments(value, scope, index); break;
            case BirPropertySet value: value.Value = C(value.Value, FieldTarget(value, scope, index)); break;
            case BirArraySet value: value.Value = C(value.Value, value.Elem.OrDefault()); break;
            case BirStackSet value: value.Value = C(value.Value, value.Elem.OrDefault()); break;
            case BirByRefStore value: value.Value = C(value.Value, value.Elem.OrDefault()); break;
            case BirNewArray value: CoerceArray(value.Elems.OrDefault(), value.Elem.OrDefault(), scope, index); break;
            case BirNewList value: CoerceArray(value.Elems.OrDefault(), value.Elem.OrDefault(), scope, index); break;
            case BirConditional value:
                value.Then = C(value.Then, value.Type.OrDefault()); value.Else = C(value.Else, value.Type.OrDefault()); break;
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
        if (node.Recv.HasValue) node.Recv = Field(CoerceExpression(node.Recv.Value, invoke.DeclaringType.OrDefault(), scope, index));
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
        CoerceVector(args, targets, scope, index);
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
        if (!resultUsed && actual != null && plainCall)
        { ReplaceResultStamps(node, actual); return node; }
        if (node is BirFieldGet or BirStaticFieldGet or BirLateinitGet
            && declared is TypeNode.Tv && actual is TypeNode.Tv && declared != actual)
        { ReplaceResultStamps(node, actual); return node; }
        if (IsVoid(declared) && actual != null && !IsVoid(actual))
        { ReplaceResultStamps(node, actual, includeSty: false); return node; }
        var genericObject = declared is TypeNode.Tv && actual is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" } && plainCall;
        var objectGeneric = actual is TypeNode.Tv && declared is TypeNode.Fqn { Args: null, Name: "object" or "System.Object" } && plainCall;
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
        return Cast(node, declared);
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
            return SignatureValueTypes.Of(physical);
        }
        var inferred = BirNodeType.Of(node as BirGraphNode, child => ExprType(child, scope, index),
            name => BirTypeLowering.PrimArrayElem.TryGetValue(name, out var element) ? element : null);
        return SignatureValueTypes.Of(index.ReferenceBuild ? inferred : BirTypeLowering.CanonicalExpressionResult(inferred));
    }

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
        ApplyAll(new[] { root }, () => new BirConstant { Type = new TypeNode.Fqn("kotlin.Unit"), Value = new BirLiteral.NullLiteral() }, _ => false);
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
        if (oracle(call, new Dictionary<string, TypeNode>()) != text)
            throw new InvalidOperationException("Owning physical result oracle did not close and copy the exact ref return");
        foreach (var (bound, receiver, declaringType) in delegateReceivers)
            if (Receiver(bound) is not BirCast { Type.HasValue: true, E.HasValue: true } conversion
                || conversion.Type.Value != declaringType || !ReferenceEquals(conversion.E.Value, receiver))
                throw new InvalidOperationException("Owning bound delegate lost its exact declaring receiver conversion: "
                    + bound.GetType().Name + " -> " + declaringType);
        Console.WriteLine("[typed physical values] self-test OK (conditional views, exact results, native storage, closed ref results, bound receivers)");
    }
}
