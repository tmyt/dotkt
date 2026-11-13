using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class NullableRepresentationMaterialization
{
    static void AppendParameters(BirGraphValue declaration, NullableRepresentationFrame frame, string scope = "method",
        GenericRepresentationPolicy policy = null, bool bodyOnlyStorage = false,
        IReadOnlyList<BirTypeParameter> enclosingParameters = null, NullableRepresentationFrame enclosingFrame = null)
    {
        if (frame.PhysicalArity == 0) return;
        var source = NullableRepresentationViews.TypeParameters(declaration).Value;
        var expanded = ExpandParameters(source, frame, scope);
        foreach (var sourceIndex in frame.StorageIndices)
        {
            var seen = new HashSet<int>();
            IEnumerable<TypeNode> Bounds(int index)
            {
                if (!seen.Add(index) || !source[index].Constraints.HasValue) yield break;
                foreach (var bound in source[index].Constraints.Value)
                    if (bound is TypeNode.Tv variable && variable.Scope == scope)
                    {
                        var slot = frame.PhysicalSlot(variable.I);
                        foreach (var inherited in Bounds(slot.SourceIndex)) yield return inherited;
                    }
                    else if (bound is TypeNode.Tv { Scope: "type" } enclosing && scope == "method"
                        && enclosingFrame != null && enclosingParameters != null)
                    {
                        var ownerVariable = new TypeNode.Tv("type", enclosingFrame.PhysicalSlot(enclosing.I).SourceIndex);
                        foreach (var inherited in OwnerConstrainedMethodLowering.IndependentBounds(ownerVariable, enclosingParameters)) yield return inherited;
                    }
                    else if (bound != null) yield return bound;
            }
            var projected = Bounds(sourceIndex).OfType<TypeNode.Fqn>().Select(bound => policy?.StorageConstraint(bound))
                .Where(bound => bound != null).Distinct().ToArray();
            if (projected.Length == 0) continue;
            var variable = frame.Variable(new TypeNode.Tv(scope, sourceIndex), NullableRepresentationFrame.Role.Storage);
            bool Contains(TypeNode type) => type == variable || type switch {
                TypeNode.Fqn { Args: { } args } => args.Any(Contains), TypeNode.Array array => Contains(array.Elem),
                TypeNode.Nullable nullable => Contains(nullable.Of), TypeNode.Oblivious oblivious => Contains(oblivious.Of),
                TypeNode.ByRef reference => Contains(reference.Of), TypeNode.Ptr pointer => Contains(pointer.Of),
                TypeNode.Mod modifier => Contains(modifier.M) || Contains(modifier.Of),
                TypeNode.Fn function => Contains(function.Ret) || function.DelegateParams.Any(Contains) || function.Ctx?.Any(Contains) == true,
                _ => false,
            };
            bool Mentions(BirGraphValue node)
            {
                var found = false;
                node.RewriteTypeFieldsInPlace(type => { found |= Contains(type); return type; });
                node.VisitChildren(child => found |= Mentions(child));
                return found;
            }
            bool SourceScalar(TypeNode type) => type is TypeNode.Tv scalar && scalar.Scope == scope && scalar.I == sourceIndex;
            var published = !bodyOnlyStorage && PublishedSlots(declaration).Any(Mentions);
            if (!bodyOnlyStorage && declaration is BirFunctionDeclaration method)
                published |= method.Ret.OrDefault() is { } result && Contains(result) || SourceScalar(method.RetKotlinType.OrDefault())
                    || method.Params.OrEmpty().Any(parameter => SourceScalar(parameter.KotlinType.OrDefault()));
            if (!bodyOnlyStorage && declaration is IBirTypeDeclaration)
            {
                var owner = new NullableRepresentationViews.Owner(declaration);
                published |= owner.Base is { } baseType && Contains(baseType) || owner.Interfaces.Any(Contains);
            }
            if (published)
            {
                var ordinary = frame.Variable(new TypeNode.Tv(scope, sourceIndex), NullableRepresentationFrame.Role.Ordinary);
                var constraints = expanded[ordinary.I].Constraints.OrEmpty();
                expanded[ordinary.I].Constraints = constraints.Concat(projected.Except(constraints)).ToArray();
                expanded[variable.I].Constraints = projected;
            }
            else expanded[variable.I].ErasedInnerConstraints = projected;
        }
        NullableRepresentationViews.SetTypeParameters(declaration, expanded);
    }

    static IEnumerable<BirGraphValue> PublishedSlots(BirGraphValue declaration)
    {
        if (declaration is BirFunctionDeclaration method)
        {
            foreach (var parameter in method.Params.OrEmpty()) yield return parameter;
            yield break;
        }
        if (declaration is BirFileDeclaration or IBirTypeDeclaration or BirRefTypeDeclaration)
        {
            var owner = new NullableRepresentationViews.Owner(declaration);
            foreach (var field in owner.Fields) yield return field;
            foreach (var property in owner.Properties) yield return property;
            foreach (var constructor in owner.Constructors)
                foreach (var parameter in constructor.Params.OrEmpty()) yield return parameter;
        }
    }

    static BirTypeParameter[] ExpandParameters(IReadOnlyList<BirTypeParameter> source, NullableRepresentationFrame frame, string scope = "method")
    {
        if (source.Count != frame.SourceArity) throw new InvalidOperationException("Generic parameter declarations do not match their source frame");
        return Enumerable.Range(0, frame.PhysicalArity).Select(index => {
            var slot = frame.PhysicalSlot(index);
            if (slot.Representation != NullableRepresentationFrame.Role.Ordinary)
                return new BirTypeParameter { Name = "$" + slot.Representation.ToString().ToLowerInvariant() + slot.SourceIndex };
            var parameter = (BirTypeParameter)source[slot.SourceIndex].DeepClone();
            if (frame.NullableIndices.Contains(slot.SourceIndex)) parameter.Constraints = parameter.Constraints.OrEmpty()
                .Append(frame.NullableVariable(new TypeNode.Tv(scope, slot.SourceIndex))).ToArray();
            return parameter;
        }).ToArray();
    }

    static Dictionary<int, TypeNode[]> CaptureStorageBounds(IReadOnlyList<BirTypeParameter> parameters, TypeNode[] captures,
        NullableRepresentationFrame frame, NullableRepresentationTypes mapping)
    {
        var result = new Dictionary<int, TypeNode[]>();
        foreach (var sourceIndex in frame.StorageIndices)
        {
            var visited = new HashSet<int>();
            IEnumerable<TypeNode> Bounds(int index)
            {
                if (!visited.Add(index) || parameters == null || !parameters[index].Constraints.HasValue) yield break;
                foreach (var bound in parameters[index].Constraints.Value)
                    if (bound is TypeNode.Tv variable)
                    {
                        var capturedIndex = Array.IndexOf(captures, variable);
                        if (capturedIndex >= 0) foreach (var inherited in Bounds(capturedIndex)) yield return inherited;
                    }
                    else if (bound != null) yield return bound;
            }
            var bounds = Bounds(sourceIndex).OfType<TypeNode.Fqn>().Select(mapping.StorageConstraint).Where(bound => bound != null).Distinct().ToArray();
            if (bounds.Length > 0) result[frame.Variable(new TypeNode.Tv("type", sourceIndex), NullableRepresentationFrame.Role.Storage).I] = bounds;
        }
        return result;
    }
}
