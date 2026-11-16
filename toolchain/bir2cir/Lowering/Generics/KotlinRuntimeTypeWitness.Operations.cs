using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinRuntimeTypeWitness
{
    // The three authored type operations carry the same witness operands. This
    // view shares structural access only; each owning lowering keeps its phase.
    internal sealed class ClassifierOperation(BirGraphValue node)
    {
        internal BirGraphValue Node => node;
        internal bool IsCast => node is BirCast;
        internal bool IsTest => node is BirIsInstance;
        internal static bool Admits(BirGraphValue value) => value is BirCast or BirIsInstance or BirInstanceOrNull;
        internal OptionalField<TypeNode> Type
        {
            get => node switch { BirCast v => v.Type, BirIsInstance v => v.Type, BirInstanceOrNull v => v.Type, _ => default };
            set { switch (node) { case BirCast v: v.Type = value; break; case BirIsInstance v: v.Type = value; break; case BirInstanceOrNull v: v.Type = value; break; } }
        }
        internal OptionalField<IBirExpression> Operand
        {
            get => node switch { BirCast v => v.E, BirIsInstance v => v.E, BirInstanceOrNull v => v.E, _ => default };
            set { switch (node) { case BirCast v: v.E = value; break; case BirIsInstance v: v.E = value; break; case BirInstanceOrNull v: v.E = value; break; } }
        }
        internal OptionalField<IBirExpression> Flags
        {
            get => node switch { BirCast v => v.KotlinTypeWitness, BirIsInstance v => v.KotlinTypeWitness, BirInstanceOrNull v => v.KotlinTypeWitness, _ => default };
            set { switch (node) { case BirCast v: v.KotlinTypeWitness = value; break; case BirIsInstance v: v.KotlinTypeWitness = value; break; case BirInstanceOrNull v: v.KotlinTypeWitness = value; break; } }
        }
        internal OptionalField<IBirExpression> Classifier
        {
            get => node switch { BirCast v => v.KotlinRuntimeTypeWitness, BirIsInstance v => v.KotlinRuntimeTypeWitness, BirInstanceOrNull v => v.KotlinRuntimeTypeWitness, _ => default };
            set { switch (node) { case BirCast v: v.KotlinRuntimeTypeWitness = value; break; case BirIsInstance v: v.KotlinRuntimeTypeWitness = value; break; case BirInstanceOrNull v: v.KotlinRuntimeTypeWitness = value; break; } }
        }
        internal OptionalField<IBirExpression> Predicate
        {
            get => node switch { BirCast v => v.KotlinClassifierPredicate, BirIsInstance v => v.KotlinClassifierPredicate, BirInstanceOrNull v => v.KotlinClassifierPredicate, _ => default };
            set { switch (node) { case BirCast v: v.KotlinClassifierPredicate = value; break; case BirIsInstance v: v.KotlinClassifierPredicate = value; break; case BirInstanceOrNull v: v.KotlinClassifierPredicate = value; break; } }
        }
        internal OptionalField<bool> Reified
        {
            get => node switch { BirCast v => v.ReifiedTypeOperand, BirInstanceOrNull v => v.ReifiedTypeOperand, _ => default };
            set { switch (node) { case BirCast v: v.ReifiedTypeOperand = value; break; case BirInstanceOrNull v: v.ReifiedTypeOperand = value; break; } }
        }
        internal ClassifierOperation CopyWithOperand(IBirExpression operand)
        {
            var copy = new ClassifierOperation(node.DeepClone());
            copy.Operand = OptionalField<IBirExpression>.Of(operand);
            return copy;
        }
        internal BirIsInstance CastTest(IBirExpression operand)
        {
            var cast = (BirCast)node.DeepClone();
            return new BirIsInstance { Type = cast.Type, E = OptionalField<IBirExpression>.Of(operand),
                SuspendFnTypePre = cast.SuspendFnTypePre, KotlinTypeWitness = cast.KotlinTypeWitness,
                KotlinRuntimeTypeWitness = cast.KotlinRuntimeTypeWitness, KotlinClassifierPredicate = cast.KotlinClassifierPredicate };
        }
        internal void ProjectPhysicalType(TypeNode type)
        {
            Type = type;
            if (node is BirCast { Sty.HasValue: true } cast) cast.Sty = BirGraphValue.CopyType(type);
        }
    }

    internal static void RewriteClassifierOperations(BirFileDeclaration root, Func<ClassifierOperation, BirGraphValue> rewrite)
    {
        void Walk(BirGraphSlot slot)
        {
            var node = slot.Value;
            if (node == null || node is IBirExecutablePayload) return;
            var children = new List<BirGraphSlot>();
            node.VisitChildSlots(children.Add);
            foreach (var child in children) Walk(child);
            if (ClassifierOperation.Admits(node)) slot.Replace(rewrite(new(node)));
        }
        root.VisitChildSlots(Walk);
    }

    internal static IBirExpression CopyExpression(IBirExpression value) => (IBirExpression)((BirGraphValue)value).DeepClone();
    internal static HashSet<string> ClassifierNames(BirFileDeclaration root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            node.VisitNameFields(name => names.Add(name), includeVariableNames: false);
            node.VisitChildren(Walk);
        }
        Walk(root);
        return names;
    }
    static readonly TypeNode NullableAny = new TypeNode.Nullable(new TypeNode.Fqn("kotlin.Any"));
    static readonly TypeNode Boolean = new TypeNode.Fqn("kotlin.Boolean");
    static BirConstant NullValue() => new() { Type = NullableAny, Value = new BirLiteral.NullLiteral() };
    static BirConstant BoolValue(bool value) => new() { Type = Boolean, Value = new BirLiteral.BooleanLiteral(value) };
    static BirConditional Choose(IBirExpression condition, IBirExpression yes, IBirExpression no, TypeNode type = null) => new() {
        Cond = OptionalField<IBirExpression>.Of(condition), Then = OptionalField<IBirExpression>.Of(yes), Else = OptionalField<IBirExpression>.Of(no),
        Type = type == null ? default : OptionalField<TypeNode>.Of(type),
    };
}
