using System.Reflection;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class VolatileFieldLowering
{
    internal static void SourceSelfTest()
    {
        static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException("Owning volatile fields: " + message); }
        var refs = ReferenceMetadataIndex.Build(new[] { typeof(object).Assembly.Location });
        var integer = new TypeNode.Fqn("kotlin.Int");
        var owner = new TypeNode.Fqn("VolatileOwner", new TypeNode[] { integer });
        var receiver = new BirLocal { Name = "receiver", Sty = owner };
        var argument = new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(7) };
        var field = new BirFieldGet { OwnerType = owner, Name = "value", Recv = receiver };
        var set = new BirSetField { OwnerType = owner, Name = "value", Recv = receiver, Value = argument };
        var setExpression = new BirFieldSetExpression { OwnerType = owner, Name = "value", Recv = receiver, Value = argument };
        var staticRead = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("VolatileFile"), Name = "global" };
        var staticWrite = new BirStaticFieldSet { OwnerType = new TypeNode.Fqn("VolatileFile"), Name = "global", Value = argument };
        var late = new BirLateinitGet { OwnerType = owner, Name = "later" };
        var propertyRead = new BirPropertyGet { Type = owner, Name = "value", Recv = receiver };
        var propertyWrite = new BirPropertySet { Type = owner, Name = "value", Recv = receiver, Value = argument };
        var address = new BirByRefOf { Inner = field };
        var pointer = new BirVariable { Name = "pointer", Type = new TypeNode.ByRef(integer), Init = address };
        var load = new BirByRefLoad { Local = "pointer", Elem = integer };
        var store = new BirByRefStore { Local = "pointer", Elem = integer, Value = argument };
        var plainField = new BirFieldGet { OwnerType = owner, Name = "plain", Recv = receiver,
            Volatile = OptionalField<bool>.Null };
        var plainLoad = new BirByRefLoad { Local = "plainPointer", Elem = integer, Volatile = false };
        var body = new IBirStatement[] {
            pointer, new BirVariable { Name = "plainPointer", Type = new TypeNode.ByRef(integer),
                Init = new BirByRefOf { Inner = plainField } },
            set, new BirExpressionStatement { Expr = setExpression },
            new BirExpressionStatement { Expr = staticRead }, new BirExpressionStatement { Expr = staticWrite },
            new BirExpressionStatement { Expr = late }, new BirExpressionStatement { Expr = propertyRead },
            new BirExpressionStatement { Expr = propertyWrite }, new BirExpressionStatement { Expr = load },
            new BirExpressionStatement { Expr = store }, new BirExpressionStatement { Expr = plainLoad },
        };
        var method = new BirFunctionDeclaration { Name = "accesses", Params = Array.Empty<BirParameter>(), Body = body,
            InlineBir = new byte[] { 0x80 } };
        var methods = new[] { method };
        var fields = new[] {
            new BirFieldDeclaration { Name = "value", Type = new TypeNode.Tv("type", 0), Volatile = true },
            new BirFieldDeclaration { Name = "plain", Type = new TypeNode.Tv("type", 0) },
            new BirFieldDeclaration { Name = "later", Type = new TypeNode.Fqn("kotlin.String"), Lateinit = true, Volatile = true },
        };
        var type = new BirClassDefinition { Name = "VolatileOwner", TypeParams = new[] { new BirTypeParameter { Name = "T" } },
            Fields = fields, Methods = methods };
        var file = new BirFileDeclaration { FileClass = "VolatileFile", Types = new IBirTypeDeclaration[] { type },
            Fields = new[] { new BirFieldDeclaration { Name = "global", Type = integer, Static = true, Volatile = true } } };
        var opaque = method.InlineBir.Value;
        ApplyAll(new[] { file }, refs);
        Require(field.Volatile.Value && set.Volatile.Value && setExpression.Volatile.Value && staticRead.Volatile.Value
            && staticWrite.Volatile.Value && late.Volatile.Value && propertyRead.Volatile.Value && propertyWrite.Volatile.Value,
            "one supported field/property access lost the constructed-owner or facade volatile fact");
        Require(load.Volatile.Value && store.Volatile.Value && plainField.Volatile.IsNull && !plainLoad.Volatile.Value,
            "postorder address stamping did not precede per-body pointer propagation, or marked a plain pointer");
        Require(ReferenceEquals(type.Fields.Value, fields) && ReferenceEquals(type.Methods.Value, methods)
            && ReferenceEquals(method.Body.Value, body) && ReferenceEquals(method.InlineBir.Value, opaque)
            && ReferenceEquals(set.Recv.Value, receiver) && ReferenceEquals(set.Value.Value, argument)
            && ReferenceEquals(address.Inner.Value, field), "field stamping cloned or replaced continuing operands/declarations");

        // Read a real framework volatile FieldDef; a local ordinary field with
        // that same owner/name must suppress even a previously cached ref hit.
        var reflected = typeof(System.Threading.Tasks.Task).GetFields(BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static).First(value => value.GetRequiredCustomModifiers()
                .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsVolatile"));
        var nativeOwner = new TypeNode.Fqn(reflected.DeclaringType.FullName);
        var native = new BirFieldGet { OwnerType = nativeOwner, Name = reflected.Name };
        BirFileDeclaration Root(BirFieldGet read) => new() { FileClass = "NativeVolatile", Methods = new[] {
            new BirFunctionDeclaration { Name = "read", Params = Array.Empty<BirParameter>(), Body = new IBirStatement[] {
                new BirExpressionStatement { Expr = read } } } } };
        ApplyAll(new[] { Root(native) }, refs);
        Require(native.Volatile.Value, "referenced CLR IsVolatile field was not stamped");
        var shadowed = new BirFieldGet { OwnerType = nativeOwner, Name = reflected.Name };
        var shadow = Root(shadowed);
        shadow.Types = new IBirTypeDeclaration[] { new BirClassDefinition { Name = nativeOwner.Name,
            Fields = new[] { new BirFieldDeclaration { Name = reflected.Name, Type = integer } } } };
        ApplyAll(new[] { shadow }, refs);
        Require(!shadowed.Volatile.IsPresent, "a current local nonvolatile declaration borrowed a referenced volatile field");
        Console.WriteLine("[owning volatile fields] self-test OK (current fields, exact ref fallback, pointers, presence and operand identity)");
    }
}
