using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Par2.Core;

/// <summary>
/// The PAR 2.0 Reed-Solomon engine. It builds the Vandermonde matrix from
/// the block base values, and (on repair) solves it by Gaussian elimination
/// so that the missing data and recovery blocks can be recomputed.
///
/// Base values: block i uses <c>base = Galois16.BlockBase(i)</c>. The
/// coefficient for (block i, exponent e) is <c>base^e</c>.
/// </summary>
public sealed class ReedSolomon
{
    private int _inputCount;
    private int _dataPresent;
    private int _dataMissing;
    private int[] _dataPresentIndex = new int[0];
    private int[] _dataMissingIndex = new int[0];
    private int[] _parPresentIndex = new int[0];
    private int[] _parMissingIndex = new int[0];
    private int _parPresent;
    private int _parMissing;

    // The (solved) matrix used to compute outputs from inputs. After
    // <see cref="Compute"/> the repairer reads factors from this.
    public int[,] Matrix { get; private set; } = new int[0, 0];

    // Output ordering: the order in which recovery blocks were declared
    // (SetOutput), and which inputs are present, in declaration order.
    private int _outputCount;

    /// <summary>
    /// Record the number of source data blocks and which of them are present.
    /// </summary>
    public void SetInput(IReadOnlyList<bool> present)
    {
        _inputCount = present.Count;
        _dataPresent = 0; _dataMissing = 0;
        var dp = new int[_inputCount];
        var dm = new int[_inputCount];
        for (int i = 0; i < _inputCount; i++)
        {
            if (present[i])
                dp[_dataPresent++] = i;
            else
                dm[_dataMissing++] = i;
        }
        Array.Resize(ref _dataPresentIndex, _dataPresent);
        Array.Copy(dp, _dataPresentIndex, _dataPresent);
        Array.Resize(ref _dataMissingIndex, _dataMissing);
        Array.Copy(dm, _dataMissingIndex, _dataMissing);
    }

    /// <summary>Record recovery block exponent as present or missing.</summary>
    public void SetOutput(bool present, int exponent)
    {
        if (present)
        {
            Array.Resize(ref _parPresentIndex, _parPresent + 1);
            _parPresentIndex[_parPresent++] = exponent;
        }
        else
        {
            Array.Resize(ref _parMissingIndex, _parMissing + 1);
            _parMissingIndex[_parMissing++] = exponent;
        }
        _outputCount++;
    }

    /// <summary>
    /// Build and (if needed) solve the matrix. After this call
    /// <see cref="Matrix"/> is ready. The rows are the missing data blocks
    /// (0..datamissing-1), each expressed over the present inputs.
    /// </summary>
    public bool Compute(out string? error)
    {
        error = null;
        int outcount = _dataMissing + _parMissing;
        int incount = _dataPresent + _dataMissing;

        if (_dataMissing > _parPresent)
        {
            error = "Not enough recovery blocks.";
            return false;
        }
        if (outcount == 0)
        {
            error = "No output blocks.";
            return false;
        }

        // leftmatrix: outcount x incount, rightmatrix: outcount x outcount.
        var left = new int[outcount, incount];
        int[,]? right = null;
        if (_dataMissing > 0)
            right = new int[outcount, outcount];

        // Rows 0..datamissing-1: one per present recovery block (in exponent
        // order of the *present* ones, i.e. declaration order).
        int p = 0;
        for (int row = 0; row < _dataMissing; row++)
        {
            int exponent = _parPresentIndex[p++];
            for (int col = 0; col < _dataPresent; col++)
                left[row, col] = Galois16.Pow(Galois16.BlockBase(_dataPresentIndex[col]), exponent);
            for (int col = 0; col < _dataMissing; col++)
                left[row, col + _dataPresent] = (row == col) ? 1 : 0;
            if (right != null)
            {
                for (int col = 0; col < _dataMissing; col++)
                    right[row, col] = Galois16.Pow(Galois16.BlockBase(_dataMissingIndex[col]), exponent);
                for (int col = 0; col < _parMissing; col++)
                    right[row, col + _dataMissing] = 0;
            }
        }

        // Rows datamissing..outcount-1: one per missing recovery block.
        p = 0;
        for (int row = 0; row < _parMissing; row++)
        {
            int exponent = _parMissingIndex[p++];
            int r = row + _dataMissing;
            for (int col = 0; col < _dataPresent; col++)
                left[r, col] = Galois16.Pow(Galois16.BlockBase(_dataPresentIndex[col]), exponent);
            for (int col = 0; col < _dataMissing; col++)
                left[r, col + _dataPresent] = 0;
            if (right != null)
            {
                for (int col = 0; col < _dataMissing; col++)
                    right[r, col] = Galois16.Pow(Galois16.BlockBase(_dataMissingIndex[col]), exponent);
                for (int col = 0; col < _parMissing; col++)
                    right[r, col + _dataMissing] = (row == col) ? 1 : 0;
            }
        }

        if (_dataMissing > 0 && right != null)
        {
            if (!GaussElim(outcount, incount, ref left, right, out int failedRow))
            {
                error = "Failed to solve the Reed Solomon matrix.";
                return false;
            }
        }

        // The factors to apply for each missing data block: row r (0..
        // datamissing-1) of the left matrix, over the present inputs.
        // Input order is: present data blocks first (dataPresentIndex), then
        // the present recovery blocks (parPresentIndex).
        Matrix = new int[_dataMissing, incount];
        for (int r = 0; r < _dataMissing; r++)
            for (int c = 0; c < incount; c++)
                Matrix[r, c] = left[r, c];

        return true;
    }

    /// <summary>
    /// Apply one input block to all output (missing) blocks.
    /// <paramref name="inputIndex"/> indexes the present inputs in order
    /// (present data first, then present recovery). The caller accumulates
    /// the result into <paramref name="outputs"/> (block-sized, zero padded).
    /// </summary>
    public void Process(int blockLength, int inputIndex, byte[] input, byte[][] outputs)
    {
        int outCount = _dataMissing;
        // Each outputs[outIndex] is an independent accumulator, so the missing
        // blocks can be reconstructed in parallel. (The caller feeds input
        // blocks one at a time, so this is the only safe axis to parallelise.)
        if (outCount > 1)
        {
            Parallel.For(0, outCount, outIndex =>
            {
                int factor = Matrix[outIndex, inputIndex];
                if (factor != 0)
                    AddMultiply(outputs[outIndex], blockLength, input, factor);
            });
        }
        else
        {
            for (int outIndex = 0; outIndex < outCount; outIndex++)
            {
                int factor = Matrix[outIndex, inputIndex];
                if (factor != 0)
                    AddMultiply(outputs[outIndex], blockLength, input, factor);
            }
        }
    }

    /// <summary>
    /// Accumulate input * factor (in GF(2^16), per 16-bit LE unit) into out:
    /// <c>out ^= input * factor</c>. Both are treated as arrays of 16-bit
    /// little-endian words. Uses an SSSE3/AVX2 vectorised multiply when the
    /// hardware supports it, falling back to a scalar loop otherwise. The
    /// result is identical either way (see <see cref="AddMultiplyScalar"/>).
    /// </summary>
    public static void AddMultiply(byte[] outp, int length, byte[] input, int factor)
    {
        if (factor == 0)
            return; // out ^= 0 is a no-op

        int words = length / 2;
        int done = 0;
        if (Avx2.IsSupported && words >= 16)
            done = AddMultiplySimd256(outp, input, words, factor);
        else if (Ssse3.IsSupported && words >= 8)
            done = AddMultiplySimd128(outp, input, words, factor);

        if (done < words)
            AddMultiplyScalar(outp, input, factor, done, words);
    }

    /// <summary>
    /// Reference scalar implementation of <see cref="AddMultiply"/> over the
    /// word range [<paramref name="firstWord"/>, <paramref name="endWord"/>).
    /// Kept public so tests and benchmarks can compare against the SIMD path.
    /// </summary>
    public static void AddMultiplyScalar(byte[] outp, byte[] input, int factor, int firstWord = 0, int endWord = -1)
    {
        if (endWord < 0)
            endWord = input.Length / 2;
        if (factor == 0)
            return;
        Span<ushort> dst = MemoryMarshal.Cast<byte, ushort>(outp.AsSpan());
        ReadOnlySpan<ushort> src = MemoryMarshal.Cast<byte, ushort>(input.AsSpan());
        int logF = Galois16.LogOf(factor); // hoisted: factor is constant across the call
        for (int w = firstWord; w < endWord; w++)
        {
            int a = src[w];
            if (a != 0)
                dst[w] ^= (ushort)Galois16.MultiplyByLog(a, logF);
        }
    }

    // --- SIMD GF(2^16) multiply-add (split-nibble PSHUFB method) --------------
    //
    // c*x is linear over GF(2): split each 16-bit word x into four nibbles
    //   x = n0 | n1<<4 | n2<<8 | n3<<12
    // so c*x = T0[n0] ^ T1[n1] ^ T2[n2] ^ T3[n3], where Tj[n] = c*(n<<4j).
    // Each Tj[n] is a 16-bit value; we store its low and high bytes in two
    // 16-entry tables and do all four lookups for a whole vector with PSHUFB
    // (Ssse3/Avx2 Shuffle). Low/high bytes of the words are separated with a
    // byte-shuffle (de-interleave), looked up, then re-interleaved with Unpack.

    // De-interleave masks (per 128-bit lane): gather the even (low) bytes of 8
    // words into byte lanes 0..7, and the odd (high) bytes likewise. 0x80 lanes
    // become zero under PSHUFB.
    private static readonly Vector128<byte> s_gatherLow =
        Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
    private static readonly Vector128<byte> s_gatherHigh =
        Vector128.Create((byte)1, 3, 5, 7, 9, 11, 13, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

    private static void BuildTables(int factor, Span<byte> lo, Span<byte> hi)
    {
        // lo/hi are 64 bytes each: four 16-entry byte tables (nibble positions 0..3).
        for (int j = 0; j < 4; j++)
        {
            int shift = 4 * j;
            for (int n = 0; n < 16; n++)
            {
                int v = Galois16.Multiply(factor, n << shift);
                lo[j * 16 + n] = (byte)v;
                hi[j * 16 + n] = (byte)(v >> 8);
            }
        }
    }

    private static int AddMultiplySimd128(byte[] outp, byte[] input, int words, int factor)
    {
        Span<byte> lo = stackalloc byte[64];
        Span<byte> hi = stackalloc byte[64];
        BuildTables(factor, lo, hi);
        Vector128<byte> l0 = Vector128.LoadUnsafe(ref lo[0]), l1 = Vector128.LoadUnsafe(ref lo[16]);
        Vector128<byte> l2 = Vector128.LoadUnsafe(ref lo[32]), l3 = Vector128.LoadUnsafe(ref lo[48]);
        Vector128<byte> h0 = Vector128.LoadUnsafe(ref hi[0]), h1 = Vector128.LoadUnsafe(ref hi[16]);
        Vector128<byte> h2 = Vector128.LoadUnsafe(ref hi[32]), h3 = Vector128.LoadUnsafe(ref hi[48]);
        Vector128<byte> nibMask = Vector128.Create((byte)0x0F);

        ref byte srcRef = ref input[0];
        ref byte dstRef = ref outp[0];
        int chunks = words / 8; // 8 words = 16 bytes per 128-bit vector
        for (int c = 0; c < chunks; c++)
        {
            nuint off = (nuint)(c * 16);
            Vector128<byte> v = Vector128.LoadUnsafe(ref srcRef, off);
            Vector128<byte> lowPlane = Ssse3.Shuffle(v, s_gatherLow);   // [L0..L7, 0..]
            Vector128<byte> highPlane = Ssse3.Shuffle(v, s_gatherHigh); // [H0..H7, 0..]
            Vector128<byte> n0 = Sse2.And(lowPlane, nibMask);
            Vector128<byte> n1 = Sse2.And(Sse2.ShiftRightLogical(lowPlane.AsUInt16(), 4).AsByte(), nibMask);
            Vector128<byte> n2 = Sse2.And(highPlane, nibMask);
            Vector128<byte> n3 = Sse2.And(Sse2.ShiftRightLogical(highPlane.AsUInt16(), 4).AsByte(), nibMask);

            Vector128<byte> resLow = Sse2.Xor(Sse2.Xor(Ssse3.Shuffle(l0, n0), Ssse3.Shuffle(l1, n1)),
                                              Sse2.Xor(Ssse3.Shuffle(l2, n2), Ssse3.Shuffle(l3, n3)));
            Vector128<byte> resHigh = Sse2.Xor(Sse2.Xor(Ssse3.Shuffle(h0, n0), Ssse3.Shuffle(h1, n1)),
                                               Sse2.Xor(Ssse3.Shuffle(h2, n2), Ssse3.Shuffle(h3, n3)));
            Vector128<byte> prod = Sse2.UnpackLow(resLow, resHigh); // re-interleave -> [rL0,rH0,...]
            Vector128<byte> dst = Vector128.LoadUnsafe(ref dstRef, off);
            Sse2.Xor(dst, prod).StoreUnsafe(ref dstRef, off);
        }
        return chunks * 8;
    }

    private static int AddMultiplySimd256(byte[] outp, byte[] input, int words, int factor)
    {
        Span<byte> lo = stackalloc byte[64];
        Span<byte> hi = stackalloc byte[64];
        BuildTables(factor, lo, hi);
        // Broadcast each 16-byte table into both 128-bit lanes (Avx2.Shuffle is per-lane).
        Vector256<byte> l0 = B(lo, 0), l1 = B(lo, 16), l2 = B(lo, 32), l3 = B(lo, 48);
        Vector256<byte> h0 = B(hi, 0), h1 = B(hi, 16), h2 = B(hi, 32), h3 = B(hi, 48);
        Vector256<byte> gLow = Vector256.Create(s_gatherLow, s_gatherLow);
        Vector256<byte> gHigh = Vector256.Create(s_gatherHigh, s_gatherHigh);
        Vector256<byte> nibMask = Vector256.Create((byte)0x0F);

        ref byte srcRef = ref input[0];
        ref byte dstRef = ref outp[0];
        int chunks = words / 16; // 16 words = 32 bytes per 256-bit vector
        for (int c = 0; c < chunks; c++)
        {
            nuint off = (nuint)(c * 32);
            Vector256<byte> v = Vector256.LoadUnsafe(ref srcRef, off);
            Vector256<byte> lowPlane = Avx2.Shuffle(v, gLow);
            Vector256<byte> highPlane = Avx2.Shuffle(v, gHigh);
            Vector256<byte> n0 = Avx2.And(lowPlane, nibMask);
            Vector256<byte> n1 = Avx2.And(Avx2.ShiftRightLogical(lowPlane.AsUInt16(), 4).AsByte(), nibMask);
            Vector256<byte> n2 = Avx2.And(highPlane, nibMask);
            Vector256<byte> n3 = Avx2.And(Avx2.ShiftRightLogical(highPlane.AsUInt16(), 4).AsByte(), nibMask);

            Vector256<byte> resLow = Avx2.Xor(Avx2.Xor(Avx2.Shuffle(l0, n0), Avx2.Shuffle(l1, n1)),
                                              Avx2.Xor(Avx2.Shuffle(l2, n2), Avx2.Shuffle(l3, n3)));
            Vector256<byte> resHigh = Avx2.Xor(Avx2.Xor(Avx2.Shuffle(h0, n0), Avx2.Shuffle(h1, n1)),
                                               Avx2.Xor(Avx2.Shuffle(h2, n2), Avx2.Shuffle(h3, n3)));
            Vector256<byte> prod = Avx2.UnpackLow(resLow, resHigh); // per-lane re-interleave
            Vector256<byte> dst = Vector256.LoadUnsafe(ref dstRef, off);
            Avx2.Xor(dst, prod).StoreUnsafe(ref dstRef, off);
        }
        return chunks * 16;

        static Vector256<byte> B(Span<byte> t, int o)
        {
            Vector128<byte> half = Vector128.LoadUnsafe(ref t[o]);
            return Vector256.Create(half, half);
        }
    }

    private static bool GaussElim(int rows, int cols, ref int[,] left, int[,] right, out int failedRow)
    {
        failedRow = 0;
        var order = new int[rows];
        for (int row = 0; row < rows; row++)
            order[row] = row;

        for (int row = 0; row < rows; row++)
        {
            if (right[row, row] == 0)
            {
                int pivot = row + 1;
                while (pivot < rows && right[pivot, row] == 0)
                    pivot++;
                if (pivot == rows)
                {
                    failedRow = order[row];
                    return false;
                }
                int tmp = order[row]; order[row] = order[pivot]; order[pivot] = tmp;
                for (int c = 0; c < cols; c++)
                {
                    int t = left[row, c]; left[row, c] = left[pivot, c]; left[pivot, c] = t;
                }
                for (int c = 0; c < rows; c++)
                {
                    int t = right[row, c]; right[row, c] = right[pivot, c]; right[pivot, c] = t;
                }
            }

            int pivotValue = right[row, row];
            if (pivotValue != 1)
            {
                for (int c = 0; c < cols; c++)
                    if (left[row, c] != 0)
                        left[row, c] = Galois16.Divide(left[row, c], pivotValue);
                right[row, row] = 1;
                for (int c = row + 1; c < rows; c++)
                    if (right[row, c] != 0)
                        right[row, c] = Galois16.Divide(right[row, c], pivotValue);
            }

            for (int row2 = 0; row2 < rows; row2++)
            {
                if (row == row2)
                    continue;
                int scale = right[row2, row];
                if (scale == 0)
                    continue;
                if (scale == 1)
                {
                    for (int c = 0; c < cols; c++)
                        if (left[row, c] != 0)
                            left[row2, c] ^= left[row, c];
                    for (int c = row; c < rows; c++)
                        if (right[row, c] != 0)
                            right[row2, c] ^= right[row, c];
                }
                else
                {
                    for (int c = 0; c < cols; c++)
                        if (left[row, c] != 0)
                            left[row2, c] ^= Galois16.Multiply(left[row, c], scale);
                    for (int c = row; c < rows; c++)
                        if (right[row, c] != 0)
                            right[row2, c] ^= Galois16.Multiply(right[row, c], scale);
                }
            }
        }
        return true;
    }
}
