// Migrated verify-roundtrip.sh section `roundtrip-pkg` — the collision-guard half.
// A class with the SAME simple name (`Vec`) in a DIFFERENT package than roundtrip.pkg.Vec — the two must
// NOT collide (they used to, at the root namespace). Its mere presence in this producer assembly, compiled
// alongside roundtrip.pkg.Vec, exercises the guard.
package roundtrip.pkgother

class Vec(val tag: String)
