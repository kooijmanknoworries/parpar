using System.Numerics;

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
        int incount = _dataPresent + _dataMissing;
        for (int outIndex = 0; outIndex < _dataMissing; outIndex++)
        {
            int factor = Matrix[outIndex, inputIndex];
            if (factor == 0)
                continue;
            AddMultiply(outputs[outIndex], blockLength, input, factor);
        }
    }

    /// <summary>
    /// Accumulate input * factor (in GF(2^16), per 16-bit LE unit) into out.
    /// Both are treated as arrays of 16-bit little-endian words.
    /// </summary>
    public static void AddMultiply(byte[] outp, int length, byte[] input, int factor)
    {
        int words = length / 2;
        for (int w = 0; w < words; w++)
        {
            int a = input[w * 2] | (input[w * 2 + 1] << 8);
            int r = outp[w * 2] | (outp[w * 2 + 1] << 8);
            int p = Galois16.Multiply(a, factor);
            int s = r ^ p;
            outp[w * 2] = (byte)s;
            outp[w * 2 + 1] = (byte)(s >> 8);
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
