using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

// A constructor declared on a @ClrTypeAlias type may be an adapter rather than a constructor that exists on the
// physical CLR type. For example, a Kotlin `(cause)` constructor can delegate through another alias constructor to a
// physical `(message, cause)` constructor. AliasHelperHoist drops those TypeDefs, so consume their constructor bodies
// first and carry the terminal argument vector/signature on every non-alias constructor that delegates through them.
//
// This is declaration-driven: it follows the exact `delegationSig` edges authored by kotc, never guesses a constructor
// by arity and never recognizes a library/type/member name. Original arguments are materialized before an expanded
// expression reads them, preserving Kotlin's left-to-right, exactly-once evaluation semantics.
sealed record AliasConstructorAdapter(
    string[] Parameters, TypeNode[] Signature, JsonArray Statements, JsonArray Arguments,
    TypeNode[] TerminalSignature, string CollectionFactoryKind = null);

sealed class AliasConstructorDelegationExpansion
{
    readonly IReadOnlyDictionary<string, JsonObject> _aliasTypes;
    readonly ReferenceMetadataIndex _refs;
    readonly Dictionary<(string Owner, int Index), AliasConstructorAdapter> _memo = new();
    readonly HashSet<JsonObject> _terminalConstructions = new(ReferenceEqualityComparer.Instance);

    AliasConstructorDelegationExpansion(
        IReadOnlyDictionary<string, JsonObject> aliasTypes, ReferenceMetadataIndex refs)
    {
        _aliasTypes = aliasTypes;
        _refs = refs;
    }

    public static AliasConstructorDelegationExpansion Collect(
        IEnumerable<JsonNode> roots, ReferenceMetadataIndex refs, ValueTypeOracle isValue,
        bool carryForReference)
    {
        var rootList = roots.ToList();
        var aliases = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var root in rootList)
            CollectTypes(root, refs, aliases);
        // A constructor delegation owns a call-evaluation plan just like an ordinary call.  The adapter is copied
        // out of its declaration, so lower that plan while it is still attached to the declaration and carry the
        // resulting ordered prefix with the argument vector.  All other expression lowering intentionally happens
        // after the adapter is inserted into its consumer.
        foreach (var alias in aliases.Values) CallEvalLowering.Apply(alias, isValue);
        var result = new AliasConstructorDelegationExpansion(aliases, refs);
        if (carryForReference)
            foreach (var root in rootList) result.StampReferenceCarriers(root);
        return result;
    }

    public void Apply(JsonNode root)
    {
        WalkTypes(root);
        RewriteConstructions(root);
    }

    JsonNode RewriteConstructions(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToList())
            {
                var child = obj[key];
                if (child == null) continue;
                var replacement = RewriteConstructions(child);
                if (!ReferenceEquals(child, replacement)) obj[key] = replacement;
            }
            if (Str(obj["k"]) == "new" && !_terminalConstructions.Contains(obj)
                && ExpandConstruction(obj) is JsonNode expanded)
                return RewriteConstructions(expanded);
            return obj;
        }
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var child = array[index];
                if (child == null) continue;
                var replacement = RewriteConstructions(child);
                if (!ReferenceEquals(child, replacement)) array[index] = replacement;
            }
        }
        return node;
    }

    JsonNode ExpandConstruction(JsonObject construction)
    {
        if (TypeJson.Read(construction["type"]) is not TypeNode.Fqn ownerType
            || construction["args"] is not JsonArray actualArguments
            || construction["argTypes"] is not JsonArray signatureNodes)
            return null;
        var owner = ReferenceMetadataIndex.BareOwnerFqn(ownerType.Name);
        var ownerArgs = ownerType.Args ?? Array.Empty<TypeNode>();
        var sourceSignature = signatureNodes.Select(TypeJson.Read).Select(type => type
                ?? throw new InvalidOperationException(
                    $"bir2cir: construction of alias '{owner}' contains a malformed argument type"))
            .Select(type => SupertypeGraph.SubstOwnerTvs(type, ownerArgs)).ToArray();

        AliasConstructorAdapter adapter;
        if (_aliasTypes.TryGetValue(owner, out var alias))
        {
            var selected = SelectConstructor(owner, alias, sourceSignature, ownerArgs);
            adapter = Specialize(
                Expand(owner, alias, selected.Index, new HashSet<(string Owner, int Index)>()), ownerArgs);
        }
        else if (!_refs.TryAliasConstructorAdapter(owner, sourceSignature, ownerArgs, out adapter))
            return null;
        if (SameSignature(sourceSignature, adapter.TerminalSignature) && IsIdentity(adapter)) return null;
        if (actualArguments.Count != adapter.Parameters.Length)
            throw new InvalidOperationException(
                $"bir2cir: construction of alias '{owner}' carries {actualArguments.Count} arguments for a "
                + $"{adapter.Parameters.Length}-parameter declaration");

        var host = new JsonObject();
        var replacements = MaterializeArguments(
            host, actualArguments, adapter.Parameters, adapter.Signature,
            adapter.Statements, adapter.Arguments);
        var materialized = new JsonObject
        {
            ["statements"] = Substitute(adapter.Statements, replacements),
            ["arguments"] = Substitute(adapter.Arguments, replacements),
        };
        // The argument temps were just minted into a separate host object.  They are part of the consumer scope too:
        // a serialized adapter was produced in another process whose generated-name counter can have emitted the same
        // spelling.  Freshen against BOTH trees before they are joined, otherwise the adapter declaration shadows the
        // argument temp and its initializer reads its own not-yet-initialized local.
        FreshenLocals(materialized, construction, host);

        var statements = (JsonArray)host["preStmts"]!;
        statements.Parent?.AsObject().Remove("preStmts");
        var adapterStatements = (JsonArray)materialized["statements"]!;
        foreach (var statement in adapterStatements.ToList())
        {
            statement?.Parent?.AsArray().Remove(statement);
            statements.Add(statement);
        }
        var arguments = (JsonArray)materialized["arguments"]!;
        arguments.Parent?.AsObject().Remove("arguments");

        var terminal = (JsonObject)construction.DeepClone();
        terminal["args"] = arguments;
        terminal["argTypes"] = TypeArray(adapter.TerminalSignature);
        _terminalConstructions.Add(terminal);
        return new JsonObject
        {
            ["k"] = "valueBlock",
            ["stmts"] = statements,
            ["result"] = terminal,
        };
    }

    static void CollectTypes(
        JsonNode node, ReferenceMetadataIndex refs, Dictionary<string, JsonObject> aliases)
    {
        if (node is not JsonObject root || root["types"] is not JsonArray types) return;
        foreach (var item in types.OfType<JsonObject>())
        {
            if (Str(item["name"]) is string name)
            {
                var bare = ReferenceMetadataIndex.BareOwnerFqn(name);
                if ((refs.Aliases.ContainsKey(bare) || HasClrTypeAlias(item))
                    && !aliases.TryAdd(bare, (JsonObject)item.DeepClone()))
                    throw new InvalidOperationException(
                        $"bir2cir: duplicate @ClrTypeAlias constructor declaration owner '{bare}'");
            }
            CollectTypes(item, refs, aliases);
        }
    }

    static bool HasClrTypeAlias(JsonObject type) =>
        type["attrs"] is JsonArray attributes && attributes.OfType<JsonObject>()
            .Any(attribute => TypeJson.OwnerName(attribute["attr"]) == "kotlin.clr.ClrTypeAlias");

    void WalkTypes(JsonNode node)
    {
        if (node is not JsonObject root || root["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            var name = Str(type["name"]);
            var bareName = name == null ? null : ReferenceMetadataIndex.BareOwnerFqn(name);
            if (bareName == null || !_aliasTypes.ContainsKey(bareName)) ExpandType(type);
            WalkTypes(type);
        }
    }

    void ExpandType(JsonObject type)
    {
        if (TypeJson.Read(type["base"]) is not TypeNode.Fqn baseType) return;
        var owner = ReferenceMetadataIndex.BareOwnerFqn(baseType.Name);
        var ownerArgs = baseType.Args ?? Array.Empty<TypeNode>();
        if (type["ctors"] is not JsonArray constructors) return;

        foreach (var ctor in constructors.OfType<JsonObject>())
        {
            if (ctor["thisArgs"] is JsonArray) continue;
            if (ctor["baseArgs"] is not JsonArray actualArguments) continue;
            // A constructor delegation descriptor is written in the TARGET owner's type-variable frame, not in the
            // derived constructor's frame. Close it through the constructed base before either selecting a local
            // alias declaration or matching a referenced carrier.
            var sourceSignature = ReadSignature(ctor, "delegationSig")
                .Select(type => SupertypeGraph.SubstOwnerTvs(type, ownerArgs)).ToArray();
            AliasConstructorAdapter adapter;
            if (_aliasTypes.TryGetValue(owner, out var alias))
            {
                var selected = SelectConstructor(owner, alias, sourceSignature, ownerArgs);
                adapter = Specialize(
                    Expand(owner, alias, selected.Index, new HashSet<(string Owner, int Index)>()), ownerArgs);
            }
            else if (!_refs.TryAliasConstructorAdapter(owner, sourceSignature, ownerArgs, out adapter))
                continue;
            if (SameSignature(sourceSignature, adapter.TerminalSignature)
                && IsIdentity(adapter))
                continue;
            if (actualArguments.Count != adapter.Parameters.Length)
                throw new InvalidOperationException(
                    $"bir2cir: constructor delegation into alias '{owner}' carries {actualArguments.Count} arguments "
                    + $"for a {adapter.Parameters.Length}-parameter declaration");

            var replacements = MaterializeArguments(
                ctor, actualArguments, adapter.Parameters, adapter.Signature,
                adapter.Statements, adapter.Arguments);
            var materialized = new JsonObject
            {
                ["statements"] = Substitute(adapter.Statements, replacements),
                ["arguments"] = Substitute(adapter.Arguments, replacements),
            };
            FreshenLocals(materialized, ctor);
            var statements = (JsonArray)materialized["statements"]!;
            var arguments = (JsonArray)materialized["arguments"]!;
            var pre = ctor["preStmts"] as JsonArray;
            if (pre == null) { pre = new JsonArray(); ctor["preStmts"] = pre; }
            foreach (var statement in statements.ToList())
            {
                statement?.Parent?.AsArray().Remove(statement);
                pre.Add(statement);
            }
            arguments.Parent?.AsObject().Remove("arguments");
            ctor["baseArgs"] = arguments;
            ctor["delegationSig"] = TypeArray(adapter.TerminalSignature);
        }
    }

    AliasConstructorAdapter Expand(
        string owner, JsonObject alias, int index, HashSet<(string Owner, int Index)> active)
    {
        var key = (owner, index);
        if (_memo.TryGetValue(key, out var known)) return known;
        if (!active.Add(key))
            throw new InvalidOperationException(
                $"bir2cir: cyclic @ClrTypeAlias constructor delegation at '{owner}' constructor {index}");

        var ctor = ((JsonArray)alias["ctors"]!)[index] as JsonObject
                   ?? throw new InvalidOperationException($"bir2cir: alias '{owner}' constructor {index} is not an object");
        var parameters = ReadParameters(ctor);
        var collectionFactoryKind = CollectionFactoryKind(ctor);
        AliasConstructorAdapter result;

        if (ctor["thisArgs"] is JsonArray thisArgs)
        {
            var target = SelectConstructor(owner, alias, ReadSignature(ctor, "delegationSig"), OwnArgs(alias));
            var tail = Expand(owner, alias, target.Index, active);
            result = Compose(parameters, ctor["preStmts"] as JsonArray, thisArgs, tail, owner,
                collectionFactoryKind);
        }
        else if (ctor["baseArgs"] is JsonArray baseArgs
                 && TypeJson.Read(alias["base"]) is TypeNode.Fqn baseType
                 && _aliasTypes.TryGetValue(ReferenceMetadataIndex.BareOwnerFqn(baseType.Name), out var baseAlias))
        {
            var baseOwner = ReferenceMetadataIndex.BareOwnerFqn(baseType.Name);
            var baseArgsForOwner = baseType.Args ?? Array.Empty<TypeNode>();
            var target = SelectConstructor(
                baseOwner, baseAlias, ReadSignature(ctor, "delegationSig"), baseArgsForOwner);
            var tail = Specialize(
                Expand(baseOwner, baseAlias, target.Index, active), baseArgsForOwner);
            result = Compose(parameters, ctor["preStmts"] as JsonArray, baseArgs, tail, owner,
                collectionFactoryKind);
        }
        else
        {
            var formalReads = new JsonArray(parameters.Names.Select((name, i) => (JsonNode)new JsonObject
            {
                ["sty"] = TypeJson.Write(parameters.Types[i]),
                ["k"] = "local",
                ["name"] = name,
            }).ToArray());
            result = new AliasConstructorAdapter(
                parameters.Names, parameters.Types, new JsonArray(), formalReads, parameters.Types,
                collectionFactoryKind);
        }

        active.Remove(key);
        _memo[key] = result;
        return result;
    }

    static AliasConstructorAdapter Compose(
        (string[] Names, TypeNode[] Types) parameters, JsonArray prefix, JsonArray delegationArguments,
        AliasConstructorAdapter tail, string owner, string collectionFactoryKind)
    {
        if (delegationArguments.Count != tail.Parameters.Length)
            throw new InvalidOperationException(
                $"bir2cir: alias constructor '{owner}' delegates {delegationArguments.Count} arguments to a "
                + $"{tail.Parameters.Length}-parameter declaration");
        var statements = prefix == null ? new JsonArray() : (JsonArray)prefix.DeepClone();
        var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        for (var i = 0; i < tail.Parameters.Length; i++)
        {
            var argument = delegationArguments[i]?.DeepClone()
                ?? throw new InvalidOperationException(
                    $"bir2cir: alias constructor '{owner}' has a null delegation argument");
            var local = $"cir$alias${System.Threading.Interlocked.Increment(ref _localCounter)}";
            statements.Add(new JsonObject
            {
                ["k"] = "var",
                ["name"] = local,
                ["type"] = TypeJson.Write(tail.Signature[i]),
                ["init"] = argument,
            });
            map[tail.Parameters[i]] = new JsonObject
            {
                ["sty"] = TypeJson.Write(tail.Signature[i]),
                ["k"] = "local",
                ["name"] = local,
            };
        }
        var tailStatements = (JsonArray)Substitute(tail.Statements, map);
        foreach (var statement in tailStatements.ToList())
        {
            statement?.Parent?.AsArray().Remove(statement);
            statements.Add(statement);
        }
        return new AliasConstructorAdapter(
            parameters.Names,
            parameters.Types,
            statements,
            (JsonArray)Substitute(tail.Arguments, map),
            tail.TerminalSignature,
            collectionFactoryKind);
    }

    static (int Index, JsonObject Constructor) SelectConstructor(
        string owner, JsonObject type, TypeNode[] signature, TypeNode[] ownerArgs)
    {
        if (type["ctors"] is not JsonArray constructors)
            throw new InvalidOperationException($"bir2cir: alias '{owner}' carries no constructors");
        var matches = constructors.OfType<JsonObject>()
            .Select((ctor, index) => (Index: index, Constructor: ctor))
            .Where(candidate => SameSignature(
                ReadParameters(candidate.Constructor).Types
                    .Select(parameter => SupertypeGraph.SubstOwnerTvs(parameter, ownerArgs)).ToArray(),
                signature))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"bir2cir: alias constructor '{owner}({string.Join(", ", signature.Select(TypeNode.ToJson))})' "
                + $"matched {matches.Length} source declarations; constructor delegation must identify exactly one");
        return matches[0];
    }

    static Dictionary<string, JsonNode> MaterializeArguments(
        JsonObject ctor, JsonArray arguments, string[] parameters, TypeNode[] signature,
        params JsonNode[] adapterTemplates)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        CollectNames(ctor, used);
        // Substitution copies these consumer temps into the serialized adapter before FreshenLocals runs.  Reserve
        // every adapter spelling up front so that the subsequent name-based alpha conversion can never mistake an
        // inserted consumer read for a declaration-owned read merely because two compiler processes restarted the
        // same generated-name counter.
        foreach (var template in adapterTemplates) CollectNames(template, used);
        var pre = ctor["preStmts"] as JsonArray;
        if (pre == null) { pre = new JsonArray(); ctor["preStmts"] = pre; }
        var replacements = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Count; i++)
        {
            var basis = $"cir$alias${System.Threading.Interlocked.Increment(ref _localCounter)}";
            var name = basis;
            for (var suffix = 1; !used.Add(name); suffix++) name = $"{basis}${suffix}";
            pre.Add(new JsonObject
            {
                ["k"] = "var",
                ["name"] = name,
                ["type"] = TypeJson.Write(signature[i]),
                ["init"] = arguments[i]?.DeepClone(),
            });
            replacements[parameters[i]] = new JsonObject
            {
                ["sty"] = TypeJson.Write(signature[i]),
                ["k"] = "local",
                ["name"] = name,
            };
        }
        return replacements;
    }

    // An adapter declaration's `tv(type,i)` belongs to the alias that declares it.  Reframe every type position —
    // including types nested inside its expressions — onto the constructed alias at the use site.  Method TVs are
    // deliberately untouched by SupertypeGraph.SubstOwnerTvs.
    internal static AliasConstructorAdapter Specialize(AliasConstructorAdapter adapter, TypeNode[] ownerArgs)
    {
        ownerArgs ??= Array.Empty<TypeNode>();
        return new AliasConstructorAdapter(
            adapter.Parameters,
            adapter.Signature.Select(type => SupertypeGraph.SubstOwnerTvs(type, ownerArgs)).ToArray(),
            (JsonArray)SubstituteTypes(adapter.Statements, ownerArgs),
            (JsonArray)SubstituteTypes(adapter.Arguments, ownerArgs),
            adapter.TerminalSignature.Select(type => SupertypeGraph.SubstOwnerTvs(type, ownerArgs)).ToArray(),
            adapter.CollectionFactoryKind);
    }

    static JsonNode SubstituteTypes(JsonNode node, TypeNode[] ownerArgs)
    {
        if (node is JsonObject obj)
        {
            if (obj["t"] is JsonValue)
            {
                var type = TypeJson.Read(obj)
                    ?? throw new InvalidOperationException("bir2cir: constructor adapter contains a malformed type node");
                return TypeJson.Write(SupertypeGraph.SubstOwnerTvs(type, ownerArgs));
            }
            var result = new JsonObject();
            foreach (var pair in obj)
                result[pair.Key] = pair.Value == null ? null : SubstituteTypes(pair.Value, ownerArgs);
            return result;
        }
        if (node is JsonArray array)
            return new JsonArray(array.Select(item => item == null ? null : SubstituteTypes(item, ownerArgs)).ToArray());
        return node.DeepClone();
    }

    static TypeNode[] OwnArgs(JsonObject alias)
    {
        var count = (alias["typeParams"] as JsonArray)?.Count ?? 0;
        return Enumerable.Range(0, count).Select(index => (TypeNode)new TypeNode.Tv("type", index)).ToArray();
    }

    static string CollectionFactoryKind(JsonObject ctor)
    {
        if (ctor["attrs"] is not JsonArray attributes) return null;
        foreach (var attribute in attributes.OfType<JsonObject>())
        {
            if (TypeJson.OwnerName(attribute["attr"]) != "kotlin.clr.ClrCollectionFactory"
                || attribute["args"] is not JsonArray { Count: > 0 } arguments
                || arguments[0] is not JsonObject first)
                continue;
            return Str(first["value"]);
        }
        return null;
    }

    static void FreshenLocals(JsonNode template, params JsonNode[] consumers)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var consumer in consumers) CollectNames(consumer, used);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        CollectDeclarations(template, map, used);
        RewriteLocals(template, map);

        static void CollectDeclarations(JsonNode node, IDictionary<string, string> map, ISet<string> used)
        {
            if (node is JsonObject obj)
            {
                var kind = Str(obj["k"]);
                if (kind == "var" && Str(obj["name"]) is string old && !map.ContainsKey(old))
                {
                    var fresh = old;
                    for (var suffix = 1; !used.Add(fresh); suffix++) fresh = $"{old}${suffix}";
                    map[old] = fresh;
                    obj["name"] = fresh;
                }
                if (kind is "forIn" or "for" or "forArray" or "forRange" or "forEachInline"
                        or "repeatInline" or "callInline"
                    && Str(obj["var"]) is string loop && !map.ContainsKey(loop))
                {
                    var fresh = loop;
                    for (var suffix = 1; !used.Add(fresh); suffix++) fresh = $"{loop}${suffix}";
                    map[loop] = fresh;
                    obj["var"] = fresh;
                }
                if (kind == "try" && obj["catches"] is JsonArray catches)
                    foreach (var clause in catches.OfType<JsonObject>())
                        if (Str(clause["var"]) is string caught && !map.ContainsKey(caught))
                        {
                            var fresh = caught;
                            for (var suffix = 1; !used.Add(fresh); suffix++) fresh = $"{caught}${suffix}";
                            map[caught] = fresh;
                            clause["var"] = fresh;
                        }
                foreach (var value in obj.Select(pair => pair.Value))
                    if (value != null) CollectDeclarations(value, map, used);
            }
            else if (node is JsonArray array)
                foreach (var value in array) if (value != null) CollectDeclarations(value, map, used);
        }

        static void RewriteLocals(JsonNode node, IReadOnlyDictionary<string, string> map)
        {
            if (node is JsonObject obj)
            {
                if (Str(obj["k"]) is "local" or "setLocal" && Str(obj["name"]) is string old
                    && map.TryGetValue(old, out var fresh)) obj["name"] = fresh;
                foreach (var value in obj.Select(pair => pair.Value))
                    if (value != null) RewriteLocals(value, map);
            }
            else if (node is JsonArray array)
                foreach (var value in array) if (value != null) RewriteLocals(value, map);
        }
    }

    static JsonNode Substitute(JsonNode node, IReadOnlyDictionary<string, JsonNode> replacements)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["k"]) == "local" && Str(obj["name"]) is string name
                && replacements.TryGetValue(name, out var replacement))
                return replacement.DeepClone();
            // A carried expression may contain a nested lambda/local function, loop, or catch whose binder happens
            // to have the same source spelling as an alias-constructor parameter.  Keep substituting free outer reads
            // in the surrounding fields, but remove lexical binders while descending the region they own.  This has
            // to happen BEFORE freshening: once an inner read has been replaced with a consumer expression, renaming
            // the binder cannot recover the captured value.
            IReadOnlyDictionary<string, JsonNode> nested = replacements;
            if (obj["params"] is JsonArray parameters && parameters.Count > 0)
            {
                var blocked = parameters.OfType<JsonObject>().Select(parameter => Str(parameter["name"]))
                    .Where(name => name != null && replacements.ContainsKey(name)).ToHashSet(StringComparer.Ordinal);
                if (blocked.Count > 0)
                    nested = replacements.Where(pair => !blocked.Contains(pair.Key))
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            }
            IReadOnlyDictionary<string, JsonNode> body = nested;
            if (Str(obj["var"]) is string binder && body.ContainsKey(binder))
                body = body.Where(pair => pair.Key != binder)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var result = new JsonObject();
            foreach (var pair in obj)
            {
                var map = pair.Key == "body" ? body
                    : pair.Key == "result" ? nested
                    : replacements;
                result[pair.Key] = pair.Value == null ? null : Substitute(pair.Value, map);
            }
            return result;
        }
        if (node is JsonArray array)
        {
            var result = new JsonArray();
            foreach (var item in array) result.Add(item == null ? null : Substitute(item, replacements));
            return result;
        }
        return node.DeepClone();
    }

    static bool IsIdentity(AliasConstructorAdapter adapter)
    {
        if (adapter.Arguments.Count != adapter.Parameters.Length) return false;
        for (var i = 0; i < adapter.Parameters.Length; i++)
            if (adapter.Arguments[i] is not JsonObject read
                || Str(read["k"]) != "local"
                || Str(read["name"]) != adapter.Parameters[i])
                return false;
        return true;
    }

    static (string[] Names, TypeNode[] Types) ReadParameters(JsonObject ctor)
    {
        if (ctor["params"] is not JsonArray parameters) return (Array.Empty<string>(), Array.Empty<TypeNode>());
        var names = new string[parameters.Count];
        var types = new TypeNode[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i] as JsonObject
                ?? throw new InvalidOperationException("bir2cir: constructor parameter is not an object");
            names[i] = Str(parameter["name"])
                ?? throw new InvalidOperationException("bir2cir: constructor parameter carries no name");
            types[i] = TypeJson.Read(parameter["type"])
                ?? throw new InvalidOperationException("bir2cir: constructor parameter carries no structured type");
        }
        return (names, types);
    }

    static TypeNode[] ReadSignature(JsonObject ctor, string key)
    {
        if (ctor[key] is not JsonArray signature)
            throw new InvalidOperationException($"bir2cir: constructor carries no `{key}` signature");
        return signature.Select(TypeJson.Read).Select(type => type
            ?? throw new InvalidOperationException($"bir2cir: constructor `{key}` contains no structured type")).ToArray();
    }

    static JsonArray TypeArray(IEnumerable<TypeNode> types) =>
        new(types.Select(TypeJson.Write).ToArray());

    void StampReferenceCarriers(JsonNode node)
    {
        if (node is not JsonObject root || root["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (Str(type["name"]) is string name)
            {
                var owner = ReferenceMetadataIndex.BareOwnerFqn(name);
                if (_aliasTypes.TryGetValue(owner, out var alias) && type["ctors"] is JsonArray constructors)
                    for (var index = 0; index < constructors.Count; index++)
                        if (constructors[index] is JsonObject ctor)
                        {
                            var adapter = Expand(owner, alias, index, new HashSet<(string Owner, int Index)>());
                            var source = ReadParameters(ctor).Types;
                            // Identity alias constructors normally need no adapter carrier. A trusted collection
                            // factory marker is itself cross-module lowering metadata, so retain a carrier for it even
                            // when its physical argument vector is unchanged.
                            if (SameSignature(source, adapter.TerminalSignature) && IsIdentity(adapter)
                                && adapter.CollectionFactoryKind == null) continue;
                            var payload = new JsonObject
                            {
                                ["parameters"] = new JsonArray(adapter.Parameters
                                    .Select(parameter => (JsonNode)JsonValue.Create(parameter)).ToArray()),
                                ["signature"] = TypeArray(adapter.Signature),
                                ["statements"] = adapter.Statements.DeepClone(),
                                ["arguments"] = adapter.Arguments.DeepClone(),
                                ["terminalSignature"] = TypeArray(adapter.TerminalSignature),
                                ["collectionFactoryKind"] = adapter.CollectionFactoryKind,
                            };
                            ctor["aliasCtorAdapter"] = Convert.ToBase64String(
                                BirCarrier.EncodeBody(BirCarrier.JsonV1, payload));
                        }
            }
            StampReferenceCarriers(type);
        }
    }

    static bool SameSignature(IReadOnlyList<TypeNode> left, IReadOnlyList<TypeNode> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++) if (left[i] != right[i]) return false;
        return true;
    }

    static void CollectNames(JsonNode node, ISet<string> names)
    {
        if (node is JsonObject obj)
        {
            if (Str(obj["name"]) is string name) names.Add(name);
            if (Str(obj["var"]) is string binder) names.Add(binder);
            foreach (var value in obj.Select(pair => pair.Value)) if (value != null) CollectNames(value, names);
        }
        else if (node is JsonArray array)
            foreach (var value in array) if (value != null) CollectNames(value, names);
    }

    static string Str(JsonNode node) =>
        (node as JsonValue)?.TryGetValue<string>(out var value) == true ? value : null;

    static int _localCounter;

    internal static void SelfTest()
    {
        static JsonObject Read(string name) => new() { ["k"] = "local", ["name"] = name };
        var carried = new JsonObject
        {
            ["k"] = "block",
            ["body"] = new JsonArray
            {
                new JsonObject { ["k"] = "exprStmt", ["expr"] = Read("argument") },
                new JsonObject
                {
                    ["k"] = "try",
                    ["body"] = new JsonArray(),
                    ["catches"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["var"] = "argument",
                            ["body"] = new JsonArray
                            {
                                new JsonObject { ["k"] = "exprStmt", ["expr"] = Read("argument") },
                            },
                        },
                    },
                },
                new JsonObject
                {
                    ["k"] = "forRange",
                    ["var"] = "argument",
                    ["range"] = Read("argument"),
                    ["body"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "exprStmt", ["expr"] = Read("argument") },
                    },
                },
                new JsonObject
                {
                    ["k"] = "lambda",
                    ["params"] = new JsonArray { new JsonObject { ["name"] = "argument" } },
                    ["body"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "exprStmt", ["expr"] = Read("argument") },
                    },
                },
            },
        };
        var substituted = Substitute(carried,
            new Dictionary<string, JsonNode>(StringComparer.Ordinal) { ["argument"] = Read("consumer") }) as JsonObject;
        var body = substituted?["body"] as JsonArray;
        var outer = body?[0]?["expr"]?["name"]?.GetValue<string>();
        var caught = body?[1]?["catches"]?[0]?["body"]?[0]?["expr"]?["name"]?.GetValue<string>();
        var range = body?[2]?["range"]?["name"]?.GetValue<string>();
        var loop = body?[2]?["body"]?[0]?["expr"]?["name"]?.GetValue<string>();
        var parameter = body?[3]?["body"]?[0]?["expr"]?["name"]?.GetValue<string>();
        if (outer != "consumer" || range != "consumer" || caught != "argument" || loop != "argument"
            || parameter != "argument")
            throw new InvalidOperationException(
                "AliasConstructorDelegationExpansion self-test: lexical binder capture during adapter substitution");

        // Exercise the actual composition as well: substitution runs first, then declarations that would collide with
        // consumer locals are freshened.  The source compiler gives loop/catch variables identity-based local names;
        // keep them distinct here so this test verifies each scope and does not manufacture a spelling collision the
        // producer itself cannot emit.
        var freshening = new JsonObject
        {
            ["k"] = "block",
            ["body"] = new JsonArray
            {
                new JsonObject
                {
                    ["k"] = "try", ["body"] = new JsonArray(),
                    ["catches"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["var"] = "caught",
                            ["body"] = new JsonArray
                            {
                                new JsonObject { ["k"] = "exprStmt", ["expr"] = Read("caught") },
                            },
                        },
                    },
                },
                new JsonObject
                {
                    ["k"] = "forRange", ["var"] = "item", ["range"] = Read("argument"),
                    ["body"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "exprStmt", ["expr"] = Read("item") },
                    },
                },
                new JsonObject
                {
                    ["k"] = "lambda",
                    ["params"] = new JsonArray { new JsonObject { ["name"] = "argument" } },
                    ["body"] = new JsonArray
                    {
                        new JsonObject { ["k"] = "exprStmt", ["expr"] = Read("argument") },
                    },
                },
            },
        };
        var composed = Substitute(freshening,
            new Dictionary<string, JsonNode>(StringComparer.Ordinal) { ["argument"] = Read("consumer") });
        FreshenLocals(composed, Read("caught"), Read("item"));
        var composedBody = composed?["body"] as JsonArray;
        caught = composedBody?[0]?["catches"]?[0]?["body"]?[0]?["expr"]?["name"]?.GetValue<string>();
        range = composedBody?[1]?["range"]?["name"]?.GetValue<string>();
        loop = composedBody?[1]?["body"]?[0]?["expr"]?["name"]?.GetValue<string>();
        parameter = composedBody?[2]?["body"]?[0]?["expr"]?["name"]?.GetValue<string>();
        if (caught != "caught$1" || range != "consumer" || loop != "item$1" || parameter != "argument")
            throw new InvalidOperationException(
                "AliasConstructorDelegationExpansion self-test: substitution/freshening composition changed lexical ownership");
        Console.WriteLine("[C# alias constructor expansion] self-test OK (params + catch + loop binders; freshening)");
    }
}
