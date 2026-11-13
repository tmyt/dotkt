using System.Text.Json.Nodes;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableTvErasureCallRealign
{
    static void SourceSnapshotSelfTest()
    {
        static TypeNode.Fqn Fqn(string name) => new(name);
        static BirFunctionDeclaration Method(string name, string id, TypeNode result, params BirParameter[] parameters)
            => new() { Name = name, DeclarationId = id, Ret = result, Params = parameters };
        static void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Nullable typed snapshot: " + message); }
        var args = new TypeNode[] { new TypeNode.Tv("type", 1) };
        var returnType = new TypeNode.Fqn("Box", args) {
            Classifier = new TypeNode.SourceClassifier("demo", new[] { "Owner" }, "Box"),
        };
        var property = Method("accessor", "property-id", returnType, new BirParameter { Type = returnType });
        property.PropertyName = "property";
        property.PropertyAccessor = "get";
        var scalarNullable = new TypeNode.Nullable(new TypeNode.Tv("method", 0));
        var transport = Method("transport", "transport-id", Fqn("object"), new BirParameter {
            Type = Fqn("object"), NullableGeneric = scalarNullable,
        });
        transport.RetKotlinType = new TypeNode.Tv("method", 0);
        transport.NullableGenericRet = scalarNullable;
        var owner = new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] {
            Method("overload", "one", Fqn("First")), Method("overload", "two", Fqn("Second")),
            Method("untyped", "untyped", Fqn("First"), new BirParameter()), transport,
        }, Ctors = new[] {
            new BirConstructor { Params = new[] { new BirParameter { Type = Fqn("First") } } },
            new BirConstructor { Params = new[] { new BirParameter { Type = Fqn("Second") } } },
            new BirConstructor { Params = new[] { new BirParameter(), new BirParameter { Type = Fqn("First") } } },
        }, Fields = new[] { new BirFieldDeclaration { Name = "slot", Type = Fqn("First") } },
            Properties = new[] { new BirPropertyDeclaration { Name = "slot", Type = Fqn("Second") } } };
        var files = new[] { new BirFileDeclaration {
            FileClass = "Facade", Methods = new[] { property }, Fields = new[] {
                new BirFieldDeclaration { Name = "storage", Type = returnType },
            }, Types = new IBirTypeDeclaration[] { owner },
        }, new BirFileDeclaration { FileClass = "Other", Methods = new[] { Method("accessor", "other-id", Fqn("Other"),
            new BirParameter { Type = Fqn("Other") }) }, Types = new IBirTypeDeclaration[] {
            new BirClassDefinition { Name = "Owner", Kind = "class", Methods = new[] { Method("ignored", "ignored", Fqn("Other")) } },
        } } };
        var before = files.Select(file => file.DeepClone()).ToArray();
        var index = CollectDeclaredMemberRets(files);
        var signature = index.ByDeclarationOwner["Facade"]["property-id"];
        Assert(ReferenceEquals(signature, index.TopLevelPropertiesByOwner["Facade"]["property|get|1|0|True"][0]),
            "exact identity and property tables lost their one shared signature");
        Assert(index.TopLevel["accessor|1"] == null && index.ByOwner["Owner"]["overload|0"] == null
            && index.ByDeclarationOwner["Owner"]["one"] != null && index.ByDeclarationOwner["Owner"]["two"] != null,
            "all-method overload poisoning or exact identity changed");
        Assert(!index.ByOwner["Owner"].ContainsKey("ignored|0") && !index.ByOwner["Owner"].ContainsKey("untyped|1")
            && index.Ctors["Owner"][1] == null && !index.Ctors["Owner"].ContainsKey(2),
            "first-owner or whole-vector constructor/parameter policy changed");
        Assert(index.FileClasses.SetEquals(new[] { "Facade", "Other" }) && index.Slots["Owner"]["slot"] == null
            && index.Slots["Facade"]["storage"].Equals(returnType), "facade storage or field/property poisoning changed");
        var transported = index.ByDeclarationOwner["Owner"]["transport-id"];
        Assert(transported.NullableErasureOwnershipKnown && transported.Ret.Equals(scalarNullable)
            && transported.Params[0].Equals(scalarNullable) && transported.ParamsRefused == null,
            "source-Tv object transport did not share nullable carrier selection");
        for (var position = 0; position < files.Length; position++)
            Assert(files[position].StructurallyEquals(before[position]), "capture changed an owning declaration");
        args[0] = Fqn("Changed");
        returnType.Classifier.OwnerPath[0] = "Changed";
        Assert(((TypeNode.Fqn)signature.Ret).Args[0] == new TypeNode.Tv("type", 1)
            && ((TypeNode.Fqn)signature.Params[0]).Classifier.OwnerPath[0] == "Owner"
            && ((TypeNode.Fqn)index.Slots["Facade"]["storage"]).Args[0] == new TypeNode.Tv("type", 1),
            "snapshot retained source-owned type vectors");

        // Real late synthetic admission keeps the same slot and ownership
        // authority as initial capture; it is not a second source oracle.
        var late = Method("late", "late-id", Fqn("object"), new BirParameter { Type = Fqn("object"), NullableGeneric = scalarNullable });
        late.Generated = true;
        late.NullableGenericRet = scalarNullable;
        var dom = files[0].ToDom();
        var lateDom = late.ToDom();
        lateDom[NullableErasureOwnershipKnownKey] = true;
        dom["types"][0]["methods"].AsArray().Add(lateDom);
        CollectNewSyntheticMembers(dom, index);
        var admitted = index.ByOwner["Owner"]["late|1"];
        Assert(admitted.NullableErasureOwnershipKnown && admitted.Ret.Equals(scalarNullable)
            && admitted.Params[0].Equals(scalarNullable), "late generated member used a different carrier policy");
        var holder = new BirClassDefinition { Name = "Holder", Kind = "class", Methods = new[] {
            Method("plain", "plain-id", Fqn("Plain")),
        } };
        dom["types"].AsArray().Add(holder.ToDom());
        CollectNewSyntheticTypes(dom, index);
        Assert(index.ByDeclarationOwner["Holder"]["plain-id"].Ret == Fqn("Plain")
            && ReferenceEquals(index.ByOwner["Owner"]["late|1"], admitted),
            "late type admission rescanned an already-owned declaration");
    }
}
