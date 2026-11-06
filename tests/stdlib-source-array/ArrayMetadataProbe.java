import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;
import java.util.zip.ZipFile;
import org.jetbrains.kotlin.metadata.ProtoBuf;
import org.jetbrains.kotlin.metadata.deserialization.Flags;
import org.jetbrains.kotlin.metadata.deserialization.NameResolverImpl;
import org.jetbrains.kotlin.metadata.deserialization.TypeTable;

// Use upstream's complete reader: dll2klib's authoring subset intentionally omits type tables.
class ArrayMetadataProbe {
    public static void main(String[] args) throws Exception {
        Set<String> wanted = Set.of("Array", "ByteArray", "ShortArray", "IntArray", "LongArray",
            "FloatArray", "DoubleArray", "CharArray", "BooleanArray", "UByteArray",
            "UShortArray", "UIntArray", "ULongArray");
        Set<String> found = new HashSet<>();
        try (ZipFile archive = new ZipFile(args[0])) {
            var entries = archive.entries();
            while (entries.hasMoreElements()) {
                var entry = entries.nextElement();
                if (!entry.getName().endsWith(".knm")) continue;
                ProtoBuf.PackageFragment fragment;
                try (var input = archive.getInputStream(entry)) {
                    fragment = ProtoBuf.PackageFragment.parseFrom(input);
                }
                var names = new NameResolverImpl(fragment.getStrings(), fragment.getQualifiedNames());
                for (var declaration : fragment.getClass_List()) {
                    String name = names.getQualifiedClassName(declaration.getFqName());
                    if (!name.startsWith("kotlin/")) continue;
                    String simple = name.substring("kotlin/".length());
                    if (!wanted.contains(simple)) continue;
                    if (!found.add(simple)) throw new AssertionError("duplicate class " + name);
                    var table = new TypeTable(declaration.getTypeTable());
                    var bases = new ArrayList<>(declaration.getSupertypeList());
                    for (int index : declaration.getSupertypeIdList()) bases.add(table.get(index));
                    List<String> actual = bases.stream().map(type ->
                        names.getQualifiedClassName(type.getClassName())).toList();
                    if (!actual.equals(List.of("kotlin/Any")))
                        throw new AssertionError("declaration inheritance changed: " + name + actual);
                    if (declaration.getTypeParameterCount() != (simple.equals("Array") ? 1 : 0))
                        throw new AssertionError("array generic frame changed: " + name);
                    if (Flags.IS_VALUE_CLASS.get(declaration.getFlags()) != simple.startsWith("U"))
                        throw new AssertionError("array value class status changed: " + name);
                }
            }
        }
        if (!found.equals(wanted)) throw new AssertionError("missing array declarations: " + found);
        System.out.println("all 13 array declarations retain Kotlin bases, generic frames and value-class status");
    }
}
