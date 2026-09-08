public sealed class Collision {
    public int First;
    public string Second;
    public int CallName;
    public int SameAsType;
    public int Value_1;
    public int Value { get { return 17; } }
    public int int_0 { get { return 29; } }
    public int Call() { return First + CallName; }
}
public sealed class GenericBox<T> {
    public T First;
    public int Second;
    public int Total() { return Second + 3; }
}
