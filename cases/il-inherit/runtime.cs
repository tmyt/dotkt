namespace P {
    // (2) Template-method base: Run() calls a PROTECTED VIRTUAL the Kotlin subclass overrides — the WinUI
    // Application.OnLaunched pattern. If the override binds to the right vtable slot, Run() prints "derived".
    public class Base {
        public string Run() => "run:" + Tag();
        protected virtual string Tag() => "base";
    }
    // (1) Subtype assignability: Button IS-A Widget, so it must be passable where a Widget is expected.
    public class Widget { public virtual string Name() => "widget"; }
    public class Button : Widget { public override string Name() => "button"; }
    public class Host { public string Show(Widget w) => "show:" + w.Name(); }
}
