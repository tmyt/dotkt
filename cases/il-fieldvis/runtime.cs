namespace Kfc {
    public static class Refl {
        public static string FieldVis(object o, string name) {
            var f = o.GetType().GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f == null) return "<none>";
            return f.IsPrivate ? "Private" : f.IsAssembly ? "Internal" : f.IsFamily ? "Protected" : "Public";
        }
    }
}
