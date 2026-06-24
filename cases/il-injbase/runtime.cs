namespace Kfc {
    public abstract class Element { internal Element() {} public int tag; }   // no PUBLIC no-arg ctor (WinRT-like)
    public class Frame : Element { public Frame() {} }
    public class TextBox : Frame { public TextBox() {} }
    public static class Api { public static string place(Element e) => "placed:" + e.tag; }
}
