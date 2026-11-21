def storage_accesses(method, objects):
    """Return logical loads/stores and the declared physical vector they operate on."""
    nodes = list(objects(method["body"]))
    slots = {p["name"]: p["type"] for p in method.get("params", [])}
    variables = [n for n in nodes if n.get("k") == "var"]
    slots.update((n["name"], n["type"]) for n in variables)
    # The for-loop holds its source vector in a System.Array temporary.
    for n in variables:
        init = n.get("init", {})
        if n["type"].get("name") == "System.Array" and init.get("k") == "local":
            slots[n["name"]] = slots.get(init["name"])
    for n in nodes:
        ref = n.get("memberRef", {})
        if ref.get("declaringType", {}).get("name") != "DotKt.Runtime.CompilerServices.ProjectedArrayStorageKt":
            continue
        op = ref.get("name")
        if op not in ("readProjectedArray", "writeProjectedArray"):
            continue
        receiver = n["args"][0]
        vector = (slots.get(receiver["name"]) if receiver.get("k") == "local"
                  else receiver.get("calleeRet", receiver.get("ret")))
        yield op, vector, receiver
