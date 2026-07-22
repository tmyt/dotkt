# Stateful MSBuild tests

`run.sh` covers behavior that requires multiple builds against the same `obj/` tree. It generates its
projects under `build/tests-msbuild`, mutates and deletes source files between builds, and verifies that
stale compiler output is not reused. Independent project-reference behavior belongs in the NUnit suites.
