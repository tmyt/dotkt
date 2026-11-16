using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ComparableBridgeSynthesis
{
    internal static void SourceSelfTest()
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Owning Comparable bridge: " + message);
        }
        TypeNode Self() => new TypeNode.Fqn("Comparable.Source");
        TypeNode Face() => new TypeNode.Fqn("kotlin.Comparable", new[] { Self() });
        BirFunctionDeclaration Target(string name = "compareTo") => new() {
            Name = name, DeclarationId = "selected-comparable", Ret = new TypeNode.Fqn("kotlin.Int"),
            Params = new[] { new BirParameter { Name = "other", Type = Self() } },
            Overrides = new[] { new BirOverride { Owner = Face(), Member = "compareTo", Kind = "method" } },
        };
        BirClassDefinition Owner(params BirFunctionDeclaration[] methods) => new() {
            Kind = "class", Name = "Comparable.Source", Methods = methods,
            Interfaces = new TypeNode[] { new TypeNode.Fqn("Example.Marker"), Face() },
        };
        BirFileDeclaration File(params IBirTypeDeclaration[] types) => new() { Types = types };
        BirCallInstance Forward(BirFunctionDeclaration method)
        {
            var calls = new List<BirCallInstance>();
            void Walk(BirGraphValue node)
            {
                if (node == null || node is IBirExecutablePayload) return;
                if (node is BirCallInstance call) calls.Add(call);
                node.VisitChildSlots(slot => Walk(slot.Value));
            }
            Walk(method);
            Require(calls.Count == 1, "bridge must contain exactly one selected forwarding call");
            return calls.Single();
        }

        var target = Target();
        var unrelated = new BirFunctionDeclaration { Name = "compareTo", Ret = new TypeNode.Fqn("kotlin.Nothing"),
            Params = new[] { new BirParameter { Name = "text", Type = new TypeNode.Fqn("kotlin.String") } } };
        var owner = Owner(unrelated, target);
        var file = File(owner);
        var originalTypes = file.Types.Value;
        var originalInterfaces = owner.Interfaces.Value;
        var notices = new List<BirFunctionDeclaration>();
        ApplyAll(new[] { file }, type => type, (actualFile, actualOwner, bridge) => {
            Require(ReferenceEquals(actualFile, file) && ReferenceEquals(actualOwner, owner)
                && ReferenceEquals(owner.Methods.Value[^1], bridge)
                && owner.Interfaces.Value[^1] is TypeNode.Fqn { Name: "System.IComparable", Args: null },
                "materialization notice must follow actual method and interface attachment");
            notices.Add(bridge);
        });
        var generated = notices.Single();
        var forward = Forward(generated);
        Require(ReferenceEquals(file.Types.Value, originalTypes) && ReferenceEquals(file.Types.Value[0], owner)
            && ReferenceEquals(owner.Methods.Value[0], unrelated) && ReferenceEquals(owner.Methods.Value[1], target)
            && owner.Interfaces.Value.Take(originalInterfaces.Length).SequenceEqual(originalInterfaces)
            && generated.Name.Value == "CompareTo" && generated.Static.Value == false && generated.Virtual.Value
            && !generated.Override.Value && !generated.Abstract.Value && !generated.ObjectOverride.Value
            && generated.Vis.Value == "public" && !generated.DeclarationId.IsPresent
            && generated.Ret.Value.Equals(new TypeNode.Fqn("kotlin.Int"))
            && generated.Params.Value.Single().Type.Value.Equals(new TypeNode.Fqn("kotlin.Any")),
            "declaration identity, append order, explicit flags or erased slot changed");
        Require(forward.Method.Value == target.Name.Value && forward.Virtual.Value && forward.ClrOwnerResolved.Value
            && forward.Recv.Value is BirThis && forward.OwnerType.Value.Equals(Self())
            && forward.Sig.Value.Single().Equals(Self()) && !forward.DeclarationId.IsPresent
            && forward.Sty.Value.Equals(target.Ret.Value) && !forward.Ret.IsPresent
            && forward.Args.Value is [BirCast { E.Value: BirLocal { Name.Value: "obj" } } cast]
            && cast.Type.Value.Equals(Self()), "frontend target relation, cast or semantic return stamp changed");
        ApplyAll(new[] { file }, null, (_, _, _) => throw new InvalidOperationException("Comparable bridge was appended twice"));
        Require(owner.Methods.Value.Length == 3, "erased CompareTo must make synthesis idempotent");

        var neverTarget = Target();
        neverTarget.Ret = new TypeNode.Fqn("kotlin.Nothing");
        var neverOwner = Owner(neverTarget);
        ApplyAll(new[] { File(neverOwner) }, null, (_, _, _) => { });
        var neverBridge = neverOwner.Methods.Value[^1];
        Require(neverBridge.Body.Value is [BirReturn { Value.Value: BirThrowExpression }]
            && Forward(neverBridge).Sty.Value.Equals(neverTarget.Ret.Value),
            "Nothing target must terminate the physical Int32 bridge without losing its stamp");

        var ambiguous = Owner(Target(), Target("CompareTo"));
        var collapsed = Owner(Target());
        var inheritedOnly = Owner();
        inheritedOnly.Methods = default;
        var contract = new BirInterfaceDefinition { Kind = "interface", Name = "Comparable.Contract",
            Interfaces = new[] { Face() }, Methods = new[] { Target() } };
        ApplyAll(new[] { File(ambiguous, inheritedOnly, contract) }, null,
            (_, _, _) => throw new InvalidOperationException("Non-unique or interface target was synthesized"));
        Require(ambiguous.Methods.Value.Length == 2 && inheritedOnly.Methods.HasValue
            && inheritedOnly.Methods.Value.Length == 0 && inheritedOnly.Interfaces.Value.Length == 2
            && contract.Methods.Value.Length == 1, "unique-target, class-only or empty-method presence policy changed");
        ApplyAll(new[] { File(collapsed) }, _ => new TypeNode.Fqn("kotlin.Any"),
            (_, _, _) => throw new InvalidOperationException("Collapsing Comparable face was synthesized"));
        Require(collapsed.Methods.Value.Length == 1 && collapsed.Interfaces.Value.Length == 2,
            "physical Comparable collapse must suppress the additional face");
        Console.WriteLine("[source Comparable bridge] self-test OK (selected target, owning appends, Nothing, idempotence)");
    }
}
