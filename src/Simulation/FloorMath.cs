namespace ImmersiveWakes.Simulation;

// Java's Math.floorDiv and Math.floorMod. C# / and % truncate toward zero: -1 / 8 is 0 and -1 % 8 is -1, while a
// block or chunk west or north of the origin needs -1 and 7 (FINDINGS "Импульсы").
static class FloorMath
{
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if (a % b != 0 && (a ^ b) < 0) q--;
        return q;
    }

    public static int FloorMod(int a, int b)
    {
        int m = a % b;
        if (m != 0 && (m ^ b) < 0) m += b;
        return m;
    }
}
