using System.Security.Cryptography;

namespace Par2.Core;

/// <summary>
/// A 16-byte MD5 digest. The bytes are stored exactly as the reference
/// par2cmdline stores them: each of the four 32-bit state words is written
/// little-endian (so the "first" byte of the digest is the high byte of the
/// fourth state word). Value-based equality is by content.
/// </summary>
public sealed class Md5Hash : IEquatable<Md5Hash>
{
    public byte[] Data { get; }

    public Md5Hash(byte[] data)
    {
        if (data.Length != 16)
            throw new ArgumentException("MD5 hash must be 16 bytes");
        Data = data;
    }

    public static Md5Hash Zero { get; } = new(new byte[16]);

    public override string ToString()
    {
        // Natural little-endian word order, i.e. the standard MD5 digest order,
        // so it can be compared directly with md5sum output.
        var sb = new System.Text.StringBuilder(32);
        for (int i = 0; i < 16; i++)
            sb.Append(Data[i].ToString("x2"));
        return sb.ToString();
    }

    // Matches the reference par2cmdline MD5Hash::operator<, which compares
    // from the last byte backwards (index 15 = most significant). This ordering
    // determines the fileid sort order of the main packet, which in turn fixes
    // how the Reed-Solomon block bases are assigned to files, so it must match
    // the reference exactly for recovery interop.
    public int CompareTo(Md5Hash other)
    {
        for (int i = 15; i >= 0; i--)
        {
            int c = Data[i].CompareTo(other.Data[i]);
            if (c != 0)
                return c;
        }
        return 0;
    }

    public bool IsZero => Data.All(b => b == 0);

    public static bool operator <(Md5Hash a, Md5Hash b) => a.CompareTo(b) < 0;
    public static bool operator >(Md5Hash a, Md5Hash b) => a.CompareTo(b) > 0;

    public bool Equals(Md5Hash? other)
    {
        if (other is null)
            return false;
        for (int i = 0; i < 16; i++)
            if (Data[i] != other.Data[i])
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is Md5Hash other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int h = 17;
            for (int i = 0; i < 16; i++)
                h = h * 31 + Data[i];
            return h;
        }
    }
}

/// <summary>
/// Streaming MD5 matching the reference C++ MD5Context. It is a class (not a
/// struct) so it can be <see cref="Clone"d>forked> part way through a stream,
/// which the creator does to compute the 16k hash while continuing the full
/// file hash.
/// </summary>
public sealed class Md5Context
{
    private const int Buffersize = 64;

    private byte[] _block = new byte[Buffersize];
    private int _used;
    private ulong _bytes;
    private uint _a, _b, _c, _d;

    public Md5Context() => Reset();

    /// <summary>Reset the state so the context can be reused.</summary>
    public void Reset()
    {
        _a = 0x67452301u;
        _b = 0xefcdab89u;
        _c = 0x98badcfeu;
        _d = 0x10325476u;
        _used = 0;
        _bytes = 0;
        Array.Clear(_block, 0, Buffersize);
    }

    /// <summary>Create an independent copy of this context's state.</summary>
    public Md5Context Clone()
    {
        var c = new Md5Context();
        c._block = (byte[])_block.Clone();
        c._used = _used;
        c._bytes = _bytes;
        c._a = _a; c._b = _b; c._c = _c; c._d = _d;
        return c;
    }

    public void Update(byte[] buffer, int offset, int count)
    {
        Update(buffer.AsSpan(offset, count));
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        _bytes += (ulong)data.Length;
        int off = 0;

        if (_used > 0 && _used + data.Length >= Buffersize)
        {
            int have = Buffersize - _used;
            data.Slice(0, have).CopyTo(new Span<byte>(_block, _used, have));
            off = have;
            UpdateState();
            _used = 0;
        }
        while (data.Length - off >= Buffersize)
        {
            data.Slice(off, Buffersize).CopyTo(new Span<byte>(_block, 0, Buffersize));
            UpdateState();
            off += Buffersize;
        }
        if (off < data.Length)
        {
            data.Slice(off).CopyTo(new Span<byte>(_block, _used, data.Length - off));
            _used += data.Length - off;
        }
    }

    /// <summary>Feed <paramref name="count"/> zero bytes (block CRC/hash padding).</summary>
    public void UpdateZeros(int count)
    {
        _bytes += (ulong)count;
        if (_used > 0)
        {
            if (_used + count >= Buffersize)
            {
                int have = Buffersize - _used;
                Array.Clear(_block, _used, have);
                count -= have;
                UpdateState();
                _used = 0;
            }
            else
            {
                Array.Clear(_block, _used, count);
                _used += count;
                return;
            }
        }
        while (count >= Buffersize)
        {
            Array.Clear(_block, 0, Buffersize);
            UpdateState();
            count -= Buffersize;
        }
        Array.Clear(_block, _used, count);
        _used += count;
    }

    public Md5Hash Final()
    {
        ulong bits = _bytes << 3;
        int padding = _used >= Buffersize - 8
            ? Buffersize - 8 + Buffersize - _used
            : Buffersize - 8 - _used;

        var pad = new byte[padding];
        pad[0] = 0x80;
        Update(pad);

        Span<byte> len = stackalloc byte[8];
        len[0] = (byte)(bits & 0xFF);
        len[1] = (byte)((bits >> 8) & 0xFF);
        len[2] = (byte)((bits >> 16) & 0xFF);
        len[3] = (byte)((bits >> 24) & 0xFF);
        len[4] = (byte)((bits >> 32) & 0xFF);
        len[5] = (byte)((bits >> 40) & 0xFF);
        len[6] = (byte)((bits >> 48) & 0xFF);
        len[7] = (byte)((bits >> 56) & 0xFF);
        Update(len);

        var outb = new byte[16];
        outb[3] = (byte)(_a >> 24); outb[2] = (byte)(_a >> 16); outb[1] = (byte)(_a >> 8); outb[0] = (byte)_a;
        outb[7] = (byte)(_b >> 24); outb[6] = (byte)(_b >> 16); outb[5] = (byte)(_b >> 8); outb[4] = (byte)_b;
        outb[11] = (byte)(_c >> 24); outb[10] = (byte)(_c >> 16); outb[9] = (byte)(_c >> 8); outb[8] = (byte)_c;
        outb[15] = (byte)(_d >> 24); outb[14] = (byte)(_d >> 16); outb[13] = (byte)(_d >> 8); outb[12] = (byte)_d;
        return new Md5Hash(outb);
    }

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));

    // w = x + ROL(w + f(x,y,z) + m[k] + t, s)  (reference ROUND macro)
    private static uint Round(uint w, uint x, uint y, uint z, uint f, uint[] m, int k, int s, uint t)
        => x + Rotl(w + f + m[k] + t, s);

    private void UpdateState()
    {
        uint[] m = new uint[16];
        for (int i = 0; i < 16; i++)
            m[i] = (uint)(_block[i * 4] | _block[i * 4 + 1] << 8 | _block[i * 4 + 2] << 16 | _block[i * 4 + 3] << 24);

        uint f1(uint x, uint y, uint z) => (((y ^ z) & x) ^ z);
        uint f2(uint x, uint y, uint z) => (x & z) | (~z & y);
        uint f3(uint x, uint y, uint z) => x ^ y ^ z;
        uint f4(uint x, uint y, uint z) => y ^ (x | ~z);

        uint a = _a, b = _b, c = _c, d = _d;

        // Round 1
        a = Round(a, b, c, d, f1(b, c, d), m, 0, 7, 0xd76aa478u);
        d = Round(d, a, b, c, f1(a, b, c), m, 1, 12, 0xe8c7b756u);
        c = Round(c, d, a, b, f1(d, a, b), m, 2, 17, 0x242070dbu);
        b = Round(b, c, d, a, f1(c, d, a), m, 3, 22, 0xc1bdceeeu);
        a = Round(a, b, c, d, f1(b, c, d), m, 4, 7, 0xf57c0fafu);
        d = Round(d, a, b, c, f1(a, b, c), m, 5, 12, 0x4787c62au);
        c = Round(c, d, a, b, f1(d, a, b), m, 6, 17, 0xa8304613u);
        b = Round(b, c, d, a, f1(c, d, a), m, 7, 22, 0xfd469501u);
        a = Round(a, b, c, d, f1(b, c, d), m, 8, 7, 0x698098d8u);
        d = Round(d, a, b, c, f1(a, b, c), m, 9, 12, 0x8b44f7afu);
        c = Round(c, d, a, b, f1(d, a, b), m, 10, 17, 0xffff5bb1u);
        b = Round(b, c, d, a, f1(c, d, a), m, 11, 22, 0x895cd7beu);
        a = Round(a, b, c, d, f1(b, c, d), m, 12, 7, 0x6b901122u);
        d = Round(d, a, b, c, f1(a, b, c), m, 13, 12, 0xfd987193u);
        c = Round(c, d, a, b, f1(d, a, b), m, 14, 17, 0xa679438eu);
        b = Round(b, c, d, a, f1(c, d, a), m, 15, 22, 0x49b40821u);

        // Round 2
        a = Round(a, b, c, d, f2(b, c, d), m, 1, 5, 0xf61e2562u);
        d = Round(d, a, b, c, f2(a, b, c), m, 6, 9, 0xc040b340u);
        c = Round(c, d, a, b, f2(d, a, b), m, 11, 14, 0x265e5a51u);
        b = Round(b, c, d, a, f2(c, d, a), m, 0, 20, 0xe9b6c7aau);
        a = Round(a, b, c, d, f2(b, c, d), m, 5, 5, 0xd62f105du);
        d = Round(d, a, b, c, f2(a, b, c), m, 10, 9, 0x02441453u);
        c = Round(c, d, a, b, f2(d, a, b), m, 15, 14, 0xd8a1e681u);
        b = Round(b, c, d, a, f2(c, d, a), m, 4, 20, 0xe7d3fbc8u);
        a = Round(a, b, c, d, f2(b, c, d), m, 9, 5, 0x21e1cde6u);
        d = Round(d, a, b, c, f2(a, b, c), m, 14, 9, 0xc33707d6u);
        c = Round(c, d, a, b, f2(d, a, b), m, 3, 14, 0xf4d50d87u);
        b = Round(b, c, d, a, f2(c, d, a), m, 8, 20, 0x455a14edu);
        a = Round(a, b, c, d, f2(b, c, d), m, 13, 5, 0xa9e3e905u);
        d = Round(d, a, b, c, f2(a, b, c), m, 2, 9, 0xfcefa3f8u);
        c = Round(c, d, a, b, f2(d, a, b), m, 7, 14, 0x676f02d9u);
        b = Round(b, c, d, a, f2(c, d, a), m, 12, 20, 0x8d2a4c8au);

        // Round 3
        a = Round(a, b, c, d, f3(b, c, d), m, 5, 4, 0xfffa3942u);
        d = Round(d, a, b, c, f3(a, b, c), m, 8, 11, 0x8771f681u);
        c = Round(c, d, a, b, f3(d, a, b), m, 11, 16, 0x6d9d6122u);
        b = Round(b, c, d, a, f3(c, d, a), m, 14, 23, 0xfde5380cu);
        a = Round(a, b, c, d, f3(b, c, d), m, 1, 4, 0xa4beea44u);
        d = Round(d, a, b, c, f3(a, b, c), m, 4, 11, 0x4bdecfa9u);
        c = Round(c, d, a, b, f3(d, a, b), m, 7, 16, 0xf6bb4b60u);
        b = Round(b, c, d, a, f3(c, d, a), m, 10, 23, 0xbebfbc70u);
        a = Round(a, b, c, d, f3(b, c, d), m, 13, 4, 0x289b7ec6u);
        d = Round(d, a, b, c, f3(a, b, c), m, 0, 11, 0xeaa127fau);
        c = Round(c, d, a, b, f3(d, a, b), m, 3, 16, 0xd4ef3085u);
        b = Round(b, c, d, a, f3(c, d, a), m, 6, 23, 0x04881d05u);
        a = Round(a, b, c, d, f3(b, c, d), m, 9, 4, 0xd9d4d039u);
        d = Round(d, a, b, c, f3(a, b, c), m, 12, 11, 0xe6db99e5u);
        c = Round(c, d, a, b, f3(d, a, b), m, 15, 16, 0x1fa27cf8u);
        b = Round(b, c, d, a, f3(c, d, a), m, 2, 23, 0xc4ac5665u);

        // Round 4
        a = Round(a, b, c, d, f4(b, c, d), m, 0, 6, 0xf4292244u);
        d = Round(d, a, b, c, f4(a, b, c), m, 7, 10, 0x432aff97u);
        c = Round(c, d, a, b, f4(d, a, b), m, 14, 15, 0xab9423a7u);
        b = Round(b, c, d, a, f4(c, d, a), m, 5, 21, 0xfc93a039u);
        a = Round(a, b, c, d, f4(b, c, d), m, 12, 6, 0x655b59c3u);
        d = Round(d, a, b, c, f4(a, b, c), m, 3, 10, 0x8f0ccc92u);
        c = Round(c, d, a, b, f4(d, a, b), m, 10, 15, 0xffeff47du);
        b = Round(b, c, d, a, f4(c, d, a), m, 1, 21, 0x85845dd1u);
        a = Round(a, b, c, d, f4(b, c, d), m, 8, 6, 0x6fa87e4fu);
        d = Round(d, a, b, c, f4(a, b, c), m, 15, 10, 0xfe2ce6e0u);
        c = Round(c, d, a, b, f4(d, a, b), m, 6, 15, 0xa3014314u);
        b = Round(b, c, d, a, f4(c, d, a), m, 13, 21, 0x4e0811a1u);
        a = Round(a, b, c, d, f4(b, c, d), m, 4, 6, 0xf7537e82u);
        d = Round(d, a, b, c, f4(a, b, c), m, 11, 10, 0xbd3af235u);
        c = Round(c, d, a, b, f4(d, a, b), m, 2, 15, 0x2ad7d2bbu);
        b = Round(b, c, d, a, f4(c, d, a), m, 9, 21, 0xeb86d391u);

        _a += a; _b += b; _c += c; _d += d;
    }

    public static Md5Hash HashBytes(ReadOnlySpan<byte> data)
    {
        var c = new Md5Context();
        c.Update(data);
        return c.Final();
    }

    public static Md5Hash HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        var c = new Md5Context();
        Span<byte> buf = stackalloc byte[81920];
        int n;
        while ((n = fs.Read(buf)) > 0)
            c.Update(buf[..n]);
        return c.Final();
    }
}
