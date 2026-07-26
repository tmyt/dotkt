# Known-failing compiler reproductions

These sources document compiler/runtime failures that cannot be green NUnit tests yet. They are deliberately excluded from normal gates and must state the failing stage in their source header.

- `generic-max/app.kt`: generic `Collection<T>.maxOrNull()` is misbound by ilemit and crashes at runtime.
