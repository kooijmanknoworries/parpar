using Par2.Core;
using Xunit;

namespace Par2.Tests;

/// <summary>
/// Verifies the SIMD <see cref="ReedSolomon.AddMultiply"/> produces byte-for-byte
/// the same result as the scalar reference, across the whole GF(2^16) factor
/// range and a variety of block sizes and alignments.
/// </summary>
public class ReedSolomonSimdTests
{
    // A straight, obviously-correct reference (the original byte-at-a-time loop),
    // independent of the code under test, so the comparison is meaningful even if
    // AddMultiplyScalar were ever changed.
    private static byte[] Reference(byte[] outp, byte[] input, int factor)
    {
        var o = (byte[])outp.Clone();
        int words = input.Length / 2;
        for (int w = 0; w < words; w++)
        {
            int a = input[w * 2] | (input[w * 2 + 1] << 8);
            int r = o[w * 2] | (o[w * 2 + 1] << 8);
            int s = r ^ Galois16.Multiply(a, factor);
            o[w * 2] = (byte)s;
            o[w * 2 + 1] = (byte)(s >> 8);
        }
        return o;
    }

    [Theory]
    [InlineData(16)]    // one AVX2 vector exactly
    [InlineData(32)]    // one SSE chunk (8 words) + ...
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(1028)]  // not a multiple of 32 bytes -> exercises the scalar tail
    [InlineData(70)]    // odd-ish, forces 128-bit + tail / pure scalar
    public void Simd_matches_reference_across_all_factors(int blockSize)
    {
        var rng = new Random(1234 + blockSize);
        var input = new byte[blockSize];
        var seed = new byte[blockSize];
        rng.NextBytes(input);
        rng.NextBytes(seed); // accumulate into non-zero output too

        for (int factor = 0; factor <= Galois16.Limit; factor++)
        {
            var expected = Reference(seed, input, factor);
            var actual = (byte[])seed.Clone();
            ReedSolomon.AddMultiply(actual, blockSize, input, factor);
            if (!actual.AsSpan().SequenceEqual(expected))
                Assert.Fail($"Mismatch at blockSize={blockSize}, factor=0x{factor:X4}");
        }
    }

    [Fact]
    public void Repairs_multiple_missing_blocks_parallel_path()
    {
        // 4 data blocks, reconstruct 2 missing ones using 2 recovery blocks.
        // dataMissing == 2 > 1, so ReedSolomon.Process takes the Parallel.For path.
        const int blocksize = 64;
        const int n = 4;
        var rng = new Random(2026);
        var data = new byte[n][];
        for (int g = 0; g < n; g++) { data[g] = new byte[blocksize]; rng.NextBytes(data[g]); }

        // recovery[e] = XOR_g data[g] * BlockBase(g)^e, for e = 0,1.
        var recovery = new byte[2][] { new byte[blocksize], new byte[blocksize] };
        for (int e = 0; e < 2; e++)
            for (int g = 0; g < n; g++)
                ReedSolomon.AddMultiply(recovery[e], blocksize, data[g], Galois16.Pow(Galois16.BlockBase(g), e));

        // Blocks 1 and 2 are missing; present inputs: data0, data3, recovery0, recovery1.
        var rs = new ReedSolomon();
        rs.SetInput(new[] { true, false, false, true });
        rs.SetOutput(true, 0);
        rs.SetOutput(true, 1);
        Assert.True(rs.Compute(out _));

        var outputs = new byte[2][] { new byte[blocksize], new byte[blocksize] };
        rs.Process(blocksize, 0, data[0], outputs);
        rs.Process(blocksize, 1, data[3], outputs);
        rs.Process(blocksize, 2, recovery[0], outputs);
        rs.Process(blocksize, 3, recovery[1], outputs);

        Assert.Equal(data[1], outputs[0]);
        Assert.Equal(data[2], outputs[1]);
    }

    [Fact]
    public void Scalar_path_matches_reference()
    {
        var rng = new Random(99);
        var input = new byte[2048];
        var outp = new byte[2048];
        rng.NextBytes(input);
        rng.NextBytes(outp);
        foreach (int factor in new[] { 0, 1, 2, 0x1234, 0x8058, 0xFFFF })
        {
            var expected = Reference(outp, input, factor);
            var actual = (byte[])outp.Clone();
            ReedSolomon.AddMultiplyScalar(actual, input, factor);
            Assert.Equal(expected, actual);
        }
    }
}
