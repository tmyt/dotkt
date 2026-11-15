using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class BackingFieldRename
{
    internal static void SourceSelfTest()
    {
        var any = new TypeNode.Fqn("object");
        BirFieldDeclaration Field(string name, bool isStatic = false) => new() { Name = name, Type = any, Static = isStatic };
        BirPropertyDeclaration Property(string name) => new() { Name = name, Type = any, GetSig = Array.Empty<TypeNode>() };
        BirFieldGet Read(string owner, string name) => new() { OwnerType = new TypeNode.Fqn(owner), Name = name, Ret = any };
        BirExpressionStatement Evaluate(IBirExpression value) => new() { Expr = OptionalField<IBirExpression>.Of(value) };
        var marker = new BirAttribute { Attr = new TypeNode.Fqn("Marker") };
        var storage = Field("value"); storage._nativeLocation = true; storage.Attrs = new[] { marker };
        var singleton = Field("value", true);
        var staticStorage = Field("shared", true);
        var plain = Field("plain");
        var getterPriority = Field("indexed");
        var setterOnly = Field("writeOnly"); setterOnly.Attrs = OptionalField<BirAttribute[]>.Null;
        var frame = new[] { new BirTypeParameter { Name = "T", Constraints = new TypeNode[] { any } } };
        var variable = new TypeNode.Tv("type", 0); storage.Type = variable;
        var baseType = new BirClassDefinition { Kind = "class", Name = "Storage`1", TypeParams = frame,
            Fields = new[] { storage, singleton, staticStorage, plain, getterPriority, setterOnly },
            Properties = new[] { Property("value"), Property("shared"),
                new BirPropertyDeclaration { Name = "indexed", GetSig = new[] { any }, SetSig = new[] { any } },
                new BirPropertyDeclaration { Name = "writeOnly", GetSig = OptionalField<TypeNode[]>.Null, SetSig = new[] { any } } } };
        var derived = new BirClassDefinition { Kind = "class", Name = "Derived", Base = new TypeNode.Fqn("Storage`1", new TypeNode[] { any }),
            Fields = new[] { Field("value") } };
        var leaf = new BirClassDefinition { Kind = "class", Name = "Leaf", Base = new TypeNode.Fqn("Storage`1", new TypeNode[] { any }) };
        var inherited = Read("Leaf", "value");
        var shadowed = Read("Derived", "value");
        var staticRead = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("Storage`1"), Name = "value" };
        var staticWrite = new BirStaticFieldSet { OwnerType = new TypeNode.Fqn("Leaf"), Name = "shared",
            Value = OptionalField<IBirExpression>.Of(new BirLocal { Name = "once" }) };
        var receiver = new BirCallStatic { Owner = new TypeNode.Fqn("Effects"), Method = "receiver", Args = Array.Empty<IBirExpression>() };
        var nested = new BirInlineLambda { Params = Array.Empty<BirLambdaParameter>(), Body = Array.Empty<IBirStatement>(),
            Result = OptionalField<IBirExpression>.Of(new BirLocal { Name = "captured" }) };
        var write = new BirSetField { OwnerType = new TypeNode.Fqn("Storage`1"), Name = "value",
            Recv = OptionalField<IBirExpression>.Of(receiver), Value = OptionalField<IBirExpression>.Of(nested) };
        var expressionWrite = new BirFieldSetExpression { OwnerType = new TypeNode.Fqn("Leaf"), Name = "writeOnly",
            Value = OptionalField<IBirExpression>.Of(new BirLocal { Name = "assigned" }) };
        var late = new BirLateinitGet { OwnerType = new TypeNode.Fqn("Storage`1"), Name = "value" };
        var saved = System.Text.Encoding.UTF8.GetBytes("{\"k\":\"field\",\"name\":\"value\",\"ownerType\":{\"t\":\"Storage`1\"}}");
        var sourceSignature = new TypeNode[] { variable };
        var body = new IBirStatement[] { Evaluate(inherited), Evaluate(shadowed), Evaluate(staticRead), Evaluate(staticWrite),
            write, Evaluate(expressionWrite), Evaluate(late) };
        var method = new BirFunctionDeclaration { Name = "use", DeclarationId = "use-id", Ret = any, Params = Array.Empty<BirParameter>(),
            TypeParams = new[] { new BirTypeParameter { Name = "M" } }, SuspendSourceParams = sourceSignature, InlineBir = saved, Body = body };
        var facadeField = Field("top", true);
        var root = new BirFileDeclaration { FileClass = "Facade", Fields = new[] { facadeField }, Properties = new[] { Property("top") },
            Methods = new[] { method }, Types = new IBirTypeDeclaration[] { baseType, derived, leaf } };
        var facadeUse = new BirStaticFieldGet { OwnerType = new TypeNode.Fqn("Facade"), Name = "top" };
        var siblingField = Field("sibling", true);
        var sibling = new BirFileDeclaration { FileClass = "Facade", Fields = new[] { siblingField }, Properties = new[] { Property("sibling") },
            Methods = new[] { new BirFunctionDeclaration { Name = "siblingUse", Body = new[] { Evaluate(facadeUse) } } } };
        var roots = new[] { root, sibling };
        var report = ApplyAll(roots);
        if (report.Count != 5 || !ReferenceEquals(report[0].Owner, root) || !ReferenceEquals(report[0].Field, facadeField)
            || report[0].OldName != "top" || report[0].NewName != Mangle("top")
            || !ReferenceEquals(report[1].Owner, baseType) || !ReferenceEquals(report[1].Field, storage)
            || !ReferenceEquals(report[4].Owner, sibling) || !ReferenceEquals(report[4].Field, siblingField))
            throw new InvalidOperationException("Backing rename lost exact field/owner occurrences or declaration order");
        if (storage.Name.Value != Mangle("value") || singleton.Name.Value != "value" || plain.Name.Value != "plain"
            || getterPriority.Name.Value != "indexed" || setterOnly.Name.Value != Mangle("writeOnly")
            || inherited.Name.Value != Mangle("value") || shadowed.Name.Value != "value" || staticRead.Name.Value != "value"
            || staticWrite.Name.Value != Mangle("shared") || expressionWrite.Name.Value != Mangle("writeOnly")
            || facadeUse.Name.Value != Mangle("top") || late.Name.Value != "value")
            throw new InvalidOperationException("Backing rename changed storage-kind, accessor-signature priority or nearest-field ownership");
        if (!ReferenceEquals(root.Types.Value[0], baseType) || !ReferenceEquals(baseType.TypeParams.Value, frame)
            || !ReferenceEquals(storage.Type.Value, variable) || !ReferenceEquals(method.Body.Value, body)
            || !ReferenceEquals(write.Recv.Value, receiver) || !ReferenceEquals(write.Value.Value, nested)
            || !ReferenceEquals(method.SuspendSourceParams.Value, sourceSignature) || !ReferenceEquals(method.InlineBir.Value, saved)
            || !storage._nativeLocation.Value || method.DeclarationId.Value != "use-id")
            throw new InvalidOperationException("Backing rename cloned operands, source frames, declarations or opaque metadata");
        if (storage.Attrs.Value.Length != 2 || !ReferenceEquals(storage.Attrs.Value[0], marker)
            || storage.Attrs.Value[1].Attr.Value is not TypeNode.Fqn { Name: CompilerGeneratedAttr }
            || !storage.Attrs.Value[1].AttrExternal.Value || storage.Attrs.Value[1].ArgTypes.Value.Length != 0
            || storage.Attrs.Value[1].Args.Value.Length != 0 || storage.Attrs.Value[1].AttrClr.IsPresent
            || setterOnly.Attrs.Value.Length != 1 || singleton.Attrs.IsPresent)
            throw new InvalidOperationException("Backing rename changed CompilerGenerated attribute order or presence");
        // Equal bare owners merge, including a later ordinary-field barrier;
        // they must not discard an earlier unrelated property-backed name.
        var first = new BirClassDefinition { Kind = "class", Name = "Merged`1", Fields = new[] { Field("a"), Field("c") },
            Properties = new[] { Property("a"), Property("c") } };
        var second = new BirClassDefinition { Kind = "class", Name = "Merged`2", Fields = new[] { Field("b"), Field("c") },
            Properties = new[] { Property("b") } };
        var a = Read("Merged", "a"); var b = Read("Merged", "b"); var c = Read("Merged", "c");
        ApplyAll(new[] { new BirFileDeclaration { FileClass = "MergeUses", Types = new IBirTypeDeclaration[] { first, second },
            Methods = new[] { new BirFunctionDeclaration { Name = "merge", Body = new[] { Evaluate(a), Evaluate(b), Evaluate(c) } } } } });
        if (a.Name.Value != Mangle("a") || b.Name.Value != Mangle("b") || c.Name.Value != "c")
            throw new InvalidOperationException("Backing rename replaced a duplicate-owner map or lost an ordinary shadow barrier");
        Console.WriteLine("[source backing fields] exact declarations, storage kinds, descriptors, ownership and metadata: OK");
    }
}
