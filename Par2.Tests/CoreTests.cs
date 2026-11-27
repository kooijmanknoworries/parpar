using Par2.Core;
using Xunit;

namespace Par2.Tests;

public class Galois16Tests
{
    // Base values as produced by the reference par2cmdline (Galois16,
    // generator 0x1100B, base[i] = antilog of the (i+1)th integer coprime
    // to 65535).
    private static readonly int[] ExpectedBases =
    {
        0x0002, 0x0004, 0x0010, 0x0080, 0x0100, 0x0800, 0x2000, 0x4000,
        0x100b, 0x8058, 0x42ec, 0x85d8, 0x6eec, 0xabbb, 0x477d, 0x0dff,
        0x1bfe, 0x4fdd, 0x9fba, 0xbdfc
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void BlockBaseMatchesReference(int i)
    {
        Assert.Equal(ExpectedBases[i], Galois16.BlockBase(i));
    }

    [Fact]
    public void Base0IsTwo()
    {
        // Block 0 has base value 2.
        Assert.Equal(2, Galois16.BlockBase(0));
        // base0^1 = 2, base0^2 = 4, base0^3 = 8.
        Assert.Equal(2, Galois16.Pow(Galois16.BlockBase(0), 1));
        Assert.Equal(4, Galois16.Pow(Galois16.BlockBase(0), 2));
        Assert.Equal(8, Galois16.Pow(Galois16.BlockBase(0), 3));
        // In GF(2^16), 2^8 = 0x100 (the generator's low byte region).
        Assert.Equal(0x100, Galois16.Pow(2, 8));
    }

    [Fact]
    public void MultiplyRoundTrips()
    {
        // 2 * 4 = 8, 2 * 2 = 4
        Assert.Equal(8, Galois16.Multiply(2, 4));
        Assert.Equal(4, Galois16.Multiply(2, 2));
        // a * 1 = a
        Assert.Equal(0x1234, Galois16.Multiply(0x1234, 1));
        // a * 0 = 0
        Assert.Equal(0, Galois16.Multiply(0x1234, 0));
    }
}

public class Md5Tests
{
    [Fact]
    public void KnownVector()
    {
        // MD5("") = d41d8cd98f00b204e9800998ecf8427e (stored as words LE)
        var empty = new byte[0];
        var h = Md5Context.HashBytes(empty);
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", h.ToString());
    }

    [Fact]
    public void KnownVectorabc()
    {
        var h = Md5Context.HashBytes(System.Text.Encoding.ASCII.GetBytes("abc"));
        // MD5("abc") = 900150983cd24fb0d6963f7d28e17f72
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", h.ToString());
    }

    [Fact]
    public void StreamingMatchesOneShot()
    {
        var data = new byte[100000];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i * 31);
        var oneShot = Md5Context.HashBytes(data);

        var ctx = new Md5Context();
        for (int i = 0; i < data.Length; i += 77)
            ctx.Update(data.AsSpan(i, Math.Min(77, data.Length - i)));
        Assert.Equal(oneShot, ctx.Final());
    }
}

public class Crc32Tests
{
    [Fact]
    public void StandardCrc32()
    {
        // Standard CRC32("123456789") = 0xCBF43926. The reference computes it as
        // ~0 ^ CRCUpdateBlock(~0 ^ 0, ...) which equals ~Update(~0, data).
        var data = System.Text.Encoding.ASCII.GetBytes("123456789");
        uint crc = ~Crc32.Update(~0u, data);
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void BlockCrcWithZeroPadding()
    {
        // BlockCrc(data, bs) must equal the CRC of the data zero padded to blocksize.
        int blocksize = 64;
        var data = new byte[40];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;
        var padded = new byte[blocksize];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        uint viaBlock = Crc32.BlockCrc(data, blocksize);
        uint viaPadded = Crc32.BlockCrc(padded, blocksize);
        Assert.Equal(viaPadded, viaBlock);
    }
}

public class ReedSolomonRoundTripTests
{
    [Fact]
    public void RecoversAMissingBlock()
    {
        int blocksize = 16;
        int baseValue0 = Galois16.BlockBase(0);
        int baseValue1 = Galois16.BlockBase(1);

        // Two data blocks, one recovery block (exponent 0).
        // recovery = block0 * base0^0 + block1 * base1^0 = block0 + block1 (XOR)
        var data0 = new byte[blocksize];
        var data1 = new byte[blocksize];
        for (int i = 0; i < blocksize; i++)
        {
            data0[i] = (byte)(i * 7);
            data1[i] = (byte)(i * 13);
        }
        var recovery = new byte[blocksize];
        ReedSolomon.AddMultiply(recovery, blocksize, data0, Galois16.Pow(baseValue0, 0));
        ReedSolomon.AddMultiply(recovery, blocksize, data1, Galois16.Pow(baseValue1, 0));

        // Now simulate: data1 missing. present = [data0, recovery].
        var rs = new ReedSolomon();
        rs.SetInput(new[] { true, false });       // block0 present, block1 missing
        rs.SetOutput(true, 0);                    // recovery exponent 0 present

        Assert.True(rs.Compute(out _));

        // Input order: present data (block0), then recovery.
        var outputs = new byte[1][] { new byte[blocksize] };
        rs.Process(blocksize, 0, data0, outputs);
        rs.Process(blocksize, 1, recovery, outputs);

        Assert.Equal(data1, outputs[0]);
    }
}
