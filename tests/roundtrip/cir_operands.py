"""Read value flow in CIR assertions without assuming that call operands are inline."""


def result_value(node):
    while node.get("k") == "valueBlock":
        node = node["result"]
    return node


def objects(value):
    if isinstance(value, dict):
        yield value
        for child in value.values():
            yield from objects(child)
    elif isinstance(value, list):
        for child in value:
            yield from objects(child)


def operand(body, call, value):
    """Follow an immutable local only in the block whose result is this call."""
    if value.get("k") != "local":
        return value
    for block in objects(body):
        if block.get("k") != "valueBlock" or block["result"] is not call:
            continue
        name = value["name"]
        definitions = [s for s in block["stmts"] if s.get("k") == "var" and s["name"] == name]
        changed = any((n.get("k") == "setLocal" and n.get("name") == name) or
                      (n.get("k") == "byrefOf" and n.get("inner") == value)
                      for n in objects(block["stmts"]))
        if len(definitions) == 1 and "init" in definitions[0] and not changed:
            return definitions[0]["init"]
    return value
