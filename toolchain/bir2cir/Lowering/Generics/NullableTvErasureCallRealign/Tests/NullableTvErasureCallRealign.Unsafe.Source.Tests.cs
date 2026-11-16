using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    static void UnsafeSourceSelfTest()
    {
        static void Need(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Post-Unsafe nullable flow: " + reason); }
        var erased = new TypeNode.Fqn("object");
        var integer = new TypeNode.Fqn("kotlin.Int");
        var nullable = new TypeNode.Nullable(integer);
        var variable = new TypeNode.Tv("method", 0);
        var nullableVariable = new TypeNode.Nullable(variable);
        BirFunctionDeclaration Entry(string name, TypeNode result, bool generated = true) => new() {
            Name = name, Ret = result, Params = Array.Empty<BirParameter>(), Generated = generated,
            TypeParams = new[] { new BirTypeParameter { Name = "M" } },
        };
        var original = Entry("original", integer, false); original.DeclarationId = "original-id";
        var owner = new BirClassDefinition { Kind = "class", Name = "Host", Methods = new[] { original } };
        var file = new BirFileDeclaration { FileClass = "Facade", Types = new IBirTypeDeclaration[] { owner } };
        var index = CollectDeclaredMemberRets(new[] { file });
        var originalSnapshot = index.ByDeclarationOwner["Host"]["original-id"];
        ProjectDeclarationApplications(index);
        original.Ret = new TypeNode.Fqn("ChangedPhysicalResult");
        var local = Entry("access", erased); local.DeclarationId = "generated-id";
        local.TypeParams = new[] { new BirTypeParameter { Name = "M" } };
        local.NullableGenericRet = nullableVariable; local.NullableErasureOwnershipKnown = true;
        local.Params = new[] { new BirParameter { Name = "value", Type = erased, NullableGeneric = nullableVariable } };
        var independentAbi = Entry("independentAbi", erased); independentAbi.NullableErasureOwnershipKnown = true;
        independentAbi.RetKotlinType = variable;
        var external = Entry("external", erased); external.NullableGenericRet = nullableVariable;
        var constructed = Entry("constructed", new TypeNode.Fqn("Box", new TypeNode[] { variable }));
        constructed.NullableErasureOwnershipKnown = true;
        constructed.NullableGenericRet = new TypeNode.Fqn("Box", new TypeNode[] { nullableVariable });
        var notGenerated = Entry("notGenerated", integer, false);
        owner.Methods = new[] { original, local, independentAbi, external, constructed, notGenerated,
            Entry("ambiguous", integer), Entry("ambiguous", new TypeNode.Fqn("kotlin.String")) };
        var holderEntry = Entry("holderEntry", erased); holderEntry.NullableErasureOwnershipKnown = true;
        holderEntry.NullableGenericRet = nullableVariable; holderEntry.DeclarationId = "holder-id";
        var holder = new BirClassDefinition { Kind = "class", Name = "Holder", Methods = new[] { holderEntry } };
        file.Types = new IBirTypeDeclaration[] { owner, holder };
        var top = Entry("top", erased); top.NullableErasureOwnershipKnown = true; top.NullableGenericRet = nullableVariable;
        file.Methods = new[] { top };
        CollectNewSyntheticTypes(file, index);
        CollectNewSyntheticMembers(file, index);
        var selected = index.ByOwner["Host"]["access|1"];
        Need(ReferenceEquals(originalSnapshot, index.ByDeclarationOwner["Host"]["original-id"])
            && originalSnapshot.Ret == integer && !index.ByDeclarationOwner["Host"].ContainsKey("generated-id")
            && !index.ByOwner["Host"].ContainsKey("notGenerated|0") && index.ByOwner["Host"]["ambiguous|0"] == null,
            "existing declaration eligibility or generated fallback poisoning changed");
        Need(selected.NullableErasureOwnershipKnown && selected.Ret == nullableVariable && selected.Params[0] == nullableVariable
            && index.ByOwner["Host"]["external|0"].Ret == erased && !index.ByOwner["Host"]["external|0"].NullableErasureOwnershipKnown
            && index.ByOwner["Host"]["independentAbi|0"].NullableErasureOwnershipKnown
            && index.ByOwner["Host"]["independentAbi|0"].Ret == erased
            && index.ByOwner["Host"]["constructed|0"].Ret == constructed.Ret.Value,
            "selected local nullable ownership, independent scalar ABI or materialized companion selection changed");
        Need(ReferenceEquals(index.ByDeclarationOwner["Holder"]["holder-id"], index.ByOwner["Holder"]["holderEntry|0"])
            && index.ByOwner["Holder"]["holderEntry|0"].NullableErasureOwnershipKnown
            && index.TopLevel["top|0"].NullableErasureOwnershipKnown && holderEntry.NullableErasureOwnershipKnown.Value,
            "new-type/member admission did not share the same pre-consumption ownership facts");

        var nested = new BirInlineLambda { Params = Array.Empty<BirLambdaParameter>(), Body = Array.Empty<IBirStatement>(),
            Result = OptionalField<IBirExpression>.Of(new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(7) }) };
        var argument = new BirValueBlock { Stmts = new IBirStatement[] { new BirVariable { Name = "callback",
            Type = new TypeNode.Fn(false, integer, Array.Empty<TypeNode>()), Init = OptionalField<IBirExpression>.Of(nested) } },
            Result = OptionalField<IBirExpression>.Of(new BirConstant { Type = integer, Value = new BirLiteral.IntegerLiteral(4) }) };
        var call = new BirCallInstance { OwnerType = new TypeNode.Fqn("Host"), Method = "access", Ret = nullable, Sty = nullable,
            TypeArgs = new TypeNode[] { integer }, Args = new IBirExpression[] { argument } };
        var receiver = new BirLocal { Name = "host", Sty = new TypeNode.Fqn("Host") };
        call.Recv = OptionalField<IBirExpression>.Of(receiver);
        var value = new BirVariable { Name = "result", Type = nullable, Init = OptionalField<IBirExpression>.Of(call) };
        var caller = Entry("caller", erased, false);
        caller.Params = new[] { new BirParameter { Name = "host", Type = new TypeNode.Fqn("Host") } };
        caller.Body = new IBirStatement[] { value };
        file.Methods = file.Methods.Value.Append(caller).ToArray();
        ApplyAfterUnsafeAccessorSynthesis(file, index, type => type.Name == "kotlin.Int", null);
        Need(value.Init.Value is BirCast { Type.Value: var target, E.Value: var inner } && target == nullable && ReferenceEquals(inner, call)
            && call.Ret.Value == erased && call.Sty.Value == erased && ReferenceEquals(call.Recv.Value, receiver)
            && call.Args.Value[0] is BirCast { Type.Value: var argumentTarget, E.Value: var actualArgument }
            && argumentTarget == erased && ReferenceEquals(actualArgument, argument)
            && argument.Stmts.Value[0] is BirVariable { Init.Value: var actualNested } && ReferenceEquals(actualNested, nested),
            "reflow did not move the exact call/argument/registered callable through the physical object seam");
        var once = file.DeepClone();
        ApplyAfterUnsafeAccessorSynthesis(file, index, type => type.Name == "kotlin.Int", null);
        Need(file.StructurallyEquals(once), "post-Unsafe body flow is not idempotent");
        ConsumeNullableErasureOwnership(file);
        Need(!local.NullableErasureOwnershipKnown.IsPresent && !holderEntry.NullableErasureOwnershipKnown.IsPresent
            && !top.NullableErasureOwnershipKnown.IsPresent && selected.NullableErasureOwnershipKnown
            && index.SourceApplicationOrder == null && _sourceApplicationOrder == null,
            "ownership consumption changed snapshot facts or revived source application order");

        var sourceField = new TypeNode.Fqn("Box", new TypeNode[] { new TypeNode.Nullable(new TypeNode.Tv("type", 0)) });
        var storage = new BirFieldDeclaration { Name = "value", Type = sourceField, _nativeLocation = true };
        var poisoned = new BirFieldDeclaration { Name = "poisoned", Type = integer };
        var fields = new BirClassDefinition { Kind = "class", Name = "Fields", Fields = new[] { storage, poisoned }, Properties = new[] {
            new BirPropertyDeclaration { Name = "value", Type = sourceField, GetSig = Array.Empty<TypeNode>() },
            new BirPropertyDeclaration { Name = "poisoned", Type = erased, GetSig = Array.Empty<TypeNode>() },
        } };
        fields.TypeParams = new[] { new BirTypeParameter { Name = "T" } };
        var fieldFile = new BirFileDeclaration { FileClass = "FieldsFile", Types = new IBirTypeDeclaration[] { fields } };
        var fieldIndex = CollectDeclaredMemberRets(new[] { fieldFile });
        ProjectDeclarationApplications(fieldIndex);
        var old = fieldIndex.Slots["Fields"]["value"];
        storage.Type = erased;
        ApplyFieldRenames(fieldIndex, BackingFieldRename.ApplyAll(new[] { fieldFile }));
        Need(fieldIndex.Slots["Fields"]["value"] == old && fieldIndex.Slots["Fields"][BackingFieldRename.Mangle("value")] == old
            && fieldIndex.Slots["Fields"]["poisoned"] == null && fieldIndex.Slots["Fields"][BackingFieldRename.Mangle("poisoned")] == null
            && storage._nativeLocation.Value, "field rename recaptured physical storage or repaired poisoned logical snapshots");
        var born = new BirFieldDeclaration { Name = "born", Type = integer };
        var bornOwner = new BirClassDefinition { Kind = "class", Name = "Born", Fields = new[] { born },
            Properties = new[] { new BirPropertyDeclaration { Name = "born", Type = integer, GetSig = Array.Empty<TypeNode>() } } };
        ApplyFieldRenames(fieldIndex, BackingFieldRename.ApplyAll(new[] { new BirFileDeclaration {
            Types = new IBirTypeDeclaration[] { bornOwner } } }));
        Need(fieldIndex.Slots["Born"][BackingFieldRename.Mangle("born")] == integer && !fieldIndex.ByOwner.ContainsKey("Born"),
            "actual new physical field admission recaptured a source owner/method catalog");
        Console.WriteLine("[source post-Unsafe nullable] canonical admission, physical flow, ownership and field transitions: OK");
    }
}
