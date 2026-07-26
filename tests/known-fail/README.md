# Known-failing compiler reproductions

These sources document compiler/runtime failures that cannot be green NUnit tests yet. They are deliberately excluded from normal gates and must state the failing stage in their source header.

- `generic-max/app.kt`: generic `Collection<T>.maxOrNull()` is misbound by ilemit and crashes at runtime.
- `localfun-capture-write/app.kt`: a local `fun` that writes a captured enclosing `var` silently loses the write
  (it is not a ref-cell capture boundary) — compiles clean, prints the wrong value at run.
- `localfun-capture-write-via-closure/app.kt`: a lambda that CALLS a capturing local `fun` passes a local that does not
  exist in the lambda's frame — bir2cir aborts. The second blocker to fixing the entry above.
