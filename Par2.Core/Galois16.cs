namespace Par2.Core;

/// <summary>
/// Arithmetic in GF(2^16) with generator polynomial 0x1100B, the field used
/// by the PAR 2.0 Reed-Solomon code. Values are plain 16-bit integers.
/// </summary>
public static class Galois16
{
    public const int Limit = 0xFFFF; // field order minus one
    public const int Generator = 0x1100B;

    private static readonly int[] Log = new int[0x10000];
    private static readonly int[] ALog = new int[0x10000];

    // base[i] is the Vandermonde base for block i: the antilog of the (i+1)th
    // non-negative integer that is relatively prime to 65535.
    private static readonly int[] s_blockBase = new int[32768];

    /// <summary>The Vandermonde base for block index <paramref name="i"/>.</summary>
    public static int BlockBase(int i) => s_blockBase[i];

    static Galois16()
    {
        int x = 1;
        for (int i = 0; i < Limit; i++)
        {
            ALog[i] = x;
            Log[x] = i;
            x <<= 1;
            if ((x & 0x10000) != 0)
                x ^= Generator;
        }

        int logbase = 0;
        for (int i = 0; i < s_blockBase.Length; i++)
        {
            while (Gcd(Limit, logbase) != 1)
                logbase++;
            s_blockBase[i] = ALog[logbase];
            logbase++;
        }
    }

    /// <summary>Field multiplication. Returns 0 if either argument is 0.</summary>
    public static int Multiply(int a, int b)
    {
        if (a == 0 || b == 0)
            return 0;
        int sum = Log[a] + Log[b];
        return sum >= Limit ? ALog[sum - Limit] : ALog[sum];
    }

    /// <summary>
    /// Field power a^e (e up to 65535). 0^0 is treated as 1, 0^n (n>0) as 0.
    /// </summary>
    public static int Pow(int a, int e)
    {
        if (e == 0)
            return 1;
        if (a == 0)
            return 0;
        long sum = (long)Log[a] * e;
        sum = (sum >> 16) + (sum & 0xFFFF);
        return sum >= Limit ? ALog[(int)sum - Limit] : ALog[(int)sum];
    }

    /// <summary>Field division a / b (b must be non-zero).</summary>
    public static int Divide(int a, int b)
    {
        if (a == 0 || b == 0)
            return 0;
        int sum = Log[a] - Log[b];
        return sum < 0 ? ALog[sum + Limit] : ALog[sum];
    }

    public static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            int t = a % b;
            a = b;
            b = t;
        }
        return a;
    }
}
