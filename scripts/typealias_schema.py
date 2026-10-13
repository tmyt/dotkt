"""Validate source declaration metadata independently of executable BIR type/frame vocabulary."""


def validate_aliases(value, path, report):
    def error(where, message):
        report(where, message)

    def shape(node, where, required, optional=()):
        if not isinstance(node, dict) or set(node) - set(required) - set(optional) or set(required) - set(node):
            error(where, "malformed current Kotlin typealias metadata object")
            return False
        return True

    def sequence(node, where, visit):
        if not isinstance(node, list):
            error(where, "expected an array in typealias declaration metadata")
            return
        for index, child in enumerate(node):
            visit(child, f"{where}[{index}]")

    def text(node, where, nonempty=False):
        if not isinstance(node, str) or nonempty and not node:
            error(where, "expected a declaration name/string")

    def integer(node, where, nonnegative=False):
        if type(node) is not int or nonnegative and node < 0:
            error(where, "expected a declaration integer")

    def boolean(node, where):
        if type(node) is not bool:
            error(where, "expected a declaration boolean")

    def classifier(node, where):
        if not shape(node, where, ("packageName", "ownerPath", "name")):
            return
        text(node["packageName"], where + "/packageName")
        text(node["name"], where + "/name", True)
        sequence(node["ownerPath"], where + "/ownerPath", lambda child, p: text(child, p, True))

    def annotation(node, where):
        if not shape(node, where, ("classifier", "arguments")):
            return
        classifier(node["classifier"], where + "/classifier")

        def argument(child, p):
            if shape(child, p, ("name", "value")):
                text(child["name"], p + "/name", True)
                constant(child["value"], p + "/value")

        sequence(node["arguments"], where + "/arguments", argument)

    def constant(node, where):
        if not isinstance(node, dict):
            error(where, "expected a Kotlin annotation constant")
            return
        kind = node.get("kind")
        fields = {
            "byte": ("value",), "char": ("value",), "short": ("value",), "int": ("value",),
            "long": ("value",), "boolean": ("value",), "string": ("value",),
            "float": ("bits",), "double": ("bits",), "class": ("classifier", "arrayDimensions"),
            "enum": ("classifier", "name"), "annotation": ("value",), "array": ("value",),
        }
        if not isinstance(kind, str) or kind not in fields:
            error(where, "unknown Kotlin annotation constant kind")
            return
        if not shape(node, where, ("kind", "unsigned") + fields[kind]):
            return
        boolean(node["unsigned"], where + "/unsigned")
        if "classifier" in node:
            classifier(node["classifier"], where + "/classifier")
        if kind == "class":
            integer(node["arrayDimensions"], where + "/arrayDimensions", True)
        elif kind == "enum":
            text(node["name"], where + "/name", True)
        elif kind == "annotation":
            annotation(node["value"], where + "/value")
        elif kind == "array":
            sequence(node["value"], where + "/value", constant)
        elif kind == "string":
            text(node["value"], where + "/value")
        else:
            key = "bits" if kind in ("float", "double") else "value"
            integer(node[key], where + "/" + key)

    def declaration(node, where):
        if not shape(node, where, ("packageName", "ownerPath", "name", "visibility", "typeParameters",
                                   "underlyingType", "expandedType", "annotations")):
            return
        classifier({key: node[key] for key in ("packageName", "ownerPath", "name")}, where)
        if node["visibility"] not in ("public", "protected", "internal", "private", "private_to_this", "local"):
            error(where + "/visibility", "unknown Kotlin declaration visibility")
        parameters = node["typeParameters"]
        if not isinstance(parameters, list):
            error(where + "/typeParameters", "expected a parameter declaration array")
            return
        ids = [parameter.get("id") for parameter in parameters if isinstance(parameter, dict)]
        if any(type(index) is not int or index < 0 for index in ids) or len(set(ids)) != len(parameters):
            error(where + "/typeParameters", "alias parameter IDs must be unique declaration-local integers")
            return

        def type_node(child, p):
            if not isinstance(child, dict) or child.get("kind") not in ("class", "alias", "parameter"):
                error(p, "unknown alias declaration type kind")
                return
            identity = "id" if child["kind"] == "parameter" else "classifier"
            if not shape(child, p, ("kind", identity, "nullable", "suspend", "definitelyNotNull", "arguments", "annotations"),
                         ("outerType", "abbreviatedType", "flexibleUpperBound", "flexibleCapabilities")):
                return
            if identity == "classifier":
                classifier(child[identity], p + "/classifier")
            elif type(child["id"]) is not int or child["id"] not in ids:
                error(p + "/id", "parameter reference does not belong to this alias declaration")
            for key in ("nullable", "suspend", "definitelyNotNull"):
                boolean(child[key], p + "/" + key)

            def argument(arg, q):
                if not isinstance(arg, dict) or arg.get("variance") not in ("inv", "in", "out", "star"):
                    error(q, "unknown Kotlin type-argument projection")
                    return
                required = ("variance",) if arg["variance"] == "star" else ("variance", "type")
                if shape(arg, q, required) and "type" in arg:
                    type_node(arg["type"], q + "/type")

            sequence(child["arguments"], p + "/arguments", argument)
            sequence(child["annotations"], p + "/annotations", annotation)
            for key in ("outerType", "abbreviatedType", "flexibleUpperBound"):
                if key in child:
                    type_node(child[key], p + "/" + key)
            if "flexibleUpperBound" in child and "flexibleCapabilities" not in child:
                error(p, "flexible type requires its frontend capability identity")
            if "flexibleCapabilities" in child:
                text(child["flexibleCapabilities"], p + "/flexibleCapabilities")

        for index, parameter in enumerate(parameters):
            p = f"{where}/typeParameters[{index}]"
            if shape(parameter, p, ("id", "name", "variance", "bounds")):
                text(parameter["name"], p + "/name", True)
                if parameter["variance"] not in ("inv", "in", "out"):
                    error(p + "/variance", "unknown Kotlin parameter variance")
                sequence(parameter["bounds"], p + "/bounds", type_node)
        type_node(node["underlyingType"], where + "/underlyingType")
        type_node(node["expandedType"], where + "/expandedType")
        sequence(node["annotations"], where + "/annotations", annotation)

    sequence(value, path, declaration)
