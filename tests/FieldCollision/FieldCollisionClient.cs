using System;
public static class FieldCollisionClient {
    public static int Main() {
        var value = new Collision { First = 5, Second = "abc", CallName = 7, SameAsType = 11, Value_1 = 13 };
        var generic = new GenericBox<string> { First = "generic", Second = 19 };
        if (value.First != 5 || value.Second != "abc" || value.CallName != 7 || value.SameAsType != 11 ||
            value.Value_1 != 13 || value.Value != 17 || value.int_0 != 29 || value.Call() != 12 ||
            generic.First != "generic" || generic.Second != 19 || generic.Total() != 22)
            throw new Exception("Cross-assembly field binding changed");
        Console.WriteLine("PASS: cross-assembly field collisions and generic field references");
        return 0;
    }
}
