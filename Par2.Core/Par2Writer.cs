using System.Text;

namespace Par2.Core;

public enum RecoveryFileScheme
{
    Variable,  // exponential allocation (default, matches par2cmdline default)
    Uniform,   // -u
    Limited    // -l (largest file limited)
}

public sealed class CreateOptions
{
    public int RedundancyPercent { get; set; } = 10;      // -r
    public int? RecoveryBlockCount { get; set; }          // -c
    public long BlockSize { get; set; }                   // -s (bytes, multiple of 4)
    public int FirstRecoveryBlock { get; set; }           // -f (default 0)
    public int RecoveryFileCount { get; set; }            // -n (0 = default)
    public RecoveryFileScheme Scheme { get; set; } = RecoveryFileScheme.Variable;
    public bool Quiet { get; set; }
    public Action<string>? Log { get; set; }

    public void Report(string message, bool onlyWhenNotQuiet = false)
    {
        if (!Quiet || !onlyWhenNotQuiet)
            Log?.Invoke(message);
    }
}

public sealed record CreateResult(
    string BaseName,
    string SetId,
    int SourceFileCount,
    int SourceBlockCount,
    int RecoveryBlockCount,
    long BlockSize,
    IReadOnlyList<string> Files);

/// <summary>
/// Creates PAR 2.0 recovery files from a set of source files. The on-disk
/// layout, packet ordering and Reed-Solomon encoding match the reference
/// par2cmdline (QuickPar-compatible) exactly.
/// </summary>
public static class Par2Writer
{
    public const string CreatorString = "Created by par2 (ParPar) version 1.0.";

    public static async Task<CreateResult> CreateAsync(CreateOptions opt, string baseName, string[] files, CancellationToken ct = default)
    {
        // Accept a full par2 name (e.g. "test.par2") or a bare stem ("test").
        // Volumes are named from the stem: test.par2, test.vol000+001.par2, ...
        if (baseName.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^5];

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(baseName))!);

        // ---- 1. Block size and counts ----
        var sizes = new long[files.Length];
        long totalSize = 0, largestSize = 0;
        for (int i = 0; i < files.Length; i++)
        {
            sizes[i] = new FileInfo(files[i]).Length;
            totalSize += sizes[i];
            if (sizes[i] > largestSize)
                largestSize = sizes[i];
        }

        long blockSize = opt.BlockSize;
        if (blockSize <= 0)
            blockSize = ComputeDefaultBlockSize(totalSize);
        blockSize = (blockSize + 3) & ~3L;
        if (blockSize < 4)
            blockSize = 4;

        var blockCounts = new int[files.Length];
        int sourceBlockCount = 0;
        for (int i = 0; i < files.Length; i++)
        {
            blockCounts[i] = (int)((sizes[i] + blockSize - 1) / blockSize);
            sourceBlockCount += blockCounts[i];
        }
        if (sourceBlockCount > Par2Format.MaxBlocksPerFile)
            throw new InvalidOperationException("Too many source blocks (max 32768).");
        if (sourceBlockCount == 0)
            throw new InvalidOperationException("No data in source files.");

        int recoveryBlockCount;
        if (opt.RecoveryBlockCount.HasValue)
            recoveryBlockCount = opt.RecoveryBlockCount.Value;
        else
            recoveryBlockCount = Math.Max((sourceBlockCount * opt.RedundancyPercent + 50) / 100, 1);
        if (recoveryBlockCount > 65536)
            throw new InvalidOperationException("Too many recovery blocks.");
        if (opt.FirstRecoveryBlock + recoveryBlockCount >= 65536)
            throw new InvalidOperationException("First recovery block number too large.");

        int recoveryFileCount = opt.RecoveryFileCount;
        if (recoveryFileCount == 0)
            for (int b = recoveryBlockCount; b > 0; b >>= 1)
                recoveryFileCount++;
        if (recoveryFileCount > recoveryBlockCount)
            throw new InvalidOperationException("Too many recovery files.");

        opt.Report($"Block size: {blockSize}");
        opt.Report($"Source file count: {files.Length}");
        opt.Report($"Source block count: {sourceBlockCount}");
        opt.Report($"Recovery block count: {recoveryBlockCount}");
        opt.Report($"Recovery file count: {recoveryFileCount}");

        // ---- 2. Read each source file: full/16k hash and per-block hash+crc ----
        var fileNames = new string[files.Length];
        var fileHashFull = new Md5Hash[files.Length];
        var fileHash16k = new Md5Hash[files.Length];
        var blockEntries = new (Md5Hash Hash, uint Crc)[files.Length][];
        for (int f = 0; f < files.Length; f++)
        {
            ct.ThrowIfCancellationRequested();
            fileNames[f] = Path.GetFileName(files[f]);
            (fileHashFull[f], fileHash16k[f], blockEntries[f]) =
                await ComputeFileInfoAsync(files[f], blockSize, blockCounts[f], ct);
        }

        // ---- 3. File descriptions, sorted by fileid ----
        var descs = new Par2Packet[files.Length];
        var fileIndex = new int[files.Length];
        for (int f = 0; f < files.Length; f++)
        {
            descs[f] = Par2Packet.BuildFileDescription(fileHashFull[f], fileHash16k[f], (ulong)sizes[f], fileNames[f]);
            Par2Packet.FinishFileDescription(descs[f]);
            fileIndex[f] = f;
        }
        Array.Sort(fileIndex, (a, b) => descs[a].FileId.CompareTo(descs[b].FileId));

        var orderedDescs = new Par2Packet[files.Length];
        var orderedFileIds = new Md5Hash[files.Length];
        var orderedFileIndex = new int[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            orderedDescs[i] = descs[fileIndex[i]];
            orderedFileIds[i] = descs[fileIndex[i]].FileId;
            orderedFileIndex[i] = fileIndex[i];
        }

        // ---- 4. Main packet and set id ----
        var main = Par2Packet.BuildMain((ulong)blockSize, orderedFileIds);
        var setId = new Md5Hash(Md5Context.HashBytes(main.Data[(Par2Format.HeaderSize + Par2Format.MainBodySize)..]).Data);

        // ---- 5. Critical packets ----
        // "criticals" is the set of critical packets (main, descriptions,
        // verification) that is interleaved into the recovery files. The
        // creator packet is written once per file (it is not interleaved),
        // matching the reference layout.
        var creator = Par2Packet.BuildCreator(CreatorString);
        var criticals = new List<Par2Packet> { main };
        for (int i = 0; i < files.Length; i++)
            criticals.Add(orderedDescs[i]);
        for (int i = 0; i < files.Length; i++)
            criticals.Add(Par2Packet.BuildVerification(orderedFileIds[i], blockEntries[orderedFileIndex[i]]));
        criticals.Add(creator);
        foreach (var cp in criticals)
            cp.Finish(setId);

        // ---- 6. Compute recovery data ----
        opt.Report("Computing Reed Solomon matrix.");
        var recoveryBuffers = await ComputeRecoveryDataAsync(files, blockSize, orderedFileIndex, blockCounts, recoveryBlockCount, opt.FirstRecoveryBlock, ct);

        // ---- 7. Allocate and write files ----
        var allocations = ComputeAllocations(recoveryBlockCount, recoveryFileCount, opt.Scheme, opt.FirstRecoveryBlock, blockSize, largestSize);

        var written = new List<string>();
        for (int fi = 0; fi < allocations.Count; fi++)
        {
            ct.ThrowIfCancellationRequested();
            var (start, count) = allocations[fi];
            string filename = fi == allocations.Count - 1
                ? baseName + ".par2"
                : VolumeName(baseName, start, count, allocations);

            var ms = new MemoryStream();
            if (count == 0)
            {
                foreach (var cp in criticals)
                    ms.Write(cp.Data);
            }
            else
            {
                int copies = 0;
                for (int t = count; t > 0; t >>= 1)
                    copies++;

                int exponent = start;
                int packetCount = 0;
                int cpIndex = criticals.Count;
                // recoveryBuffers[r] holds the block for exponent
                // firstRecoveryBlock + r, so the file starting at exponent
                // "start" must read from buffer (start - firstRecoveryBlock).
                int recoveryIndex = start - opt.FirstRecoveryBlock;

                while (exponent < start + count)
                {
                    var rec = Par2Packet.BuildRecovery(exponent, (int)blockSize);
                    Buffer.BlockCopy(recoveryBuffers[recoveryIndex], 0, rec.Data, Par2Format.RecoveryExponentOffset + 4, (int)blockSize);
                    rec.Finish(setId);
                    ms.Write(rec.Data);
                    recoveryIndex++;
                    exponent++;

                    packetCount += copies * criticals.Count;
                    while (packetCount >= count)
                    {
                        if (cpIndex == criticals.Count)
                            cpIndex = 0;
                        ms.Write(criticals[cpIndex].Data);
                        cpIndex++;
                        packetCount -= count;
                    }
                }
            }

            var bytes = ms.ToArray();
            await File.WriteAllBytesAsync(filename, bytes, ct);
            written.Add(filename);
            opt.Report($"Wrote {bytes.Length} bytes to disk", true);
        }

        return new CreateResult(
            Path.GetFileNameWithoutExtension(baseName),
            setId.ToString(),
            files.Length,
            sourceBlockCount,
            recoveryBlockCount,
            blockSize,
            written);
    }

    private static async Task<byte[][]> ComputeRecoveryDataAsync(
        string[] files, long blockSize, int[] orderedFileIndex, int[] blockCounts,
        int recoveryBlockCount, int firstRecoveryBlock, CancellationToken ct)
    {
        var recovery = new byte[recoveryBlockCount][];
        for (int r = 0; r < recoveryBlockCount; r++)
            recovery[r] = new byte[(int)blockSize];

        // Map global source block index -> (file, block within file).
        var blockFile = new int[0];
        var blockInFile = new int[0];
        int total = 0;
        for (int i = 0; i < orderedFileIndex.Length; i++)
            total += blockCounts[orderedFileIndex[i]];
        Array.Resize(ref blockFile, total);
        Array.Resize(ref blockInFile, total);
        int pos = 0;
        for (int i = 0; i < orderedFileIndex.Length; i++)
        {
            int f = orderedFileIndex[i];
            for (int b = 0; b < blockCounts[f]; b++)
            {
                blockFile[pos] = f;
                blockInFile[pos] = b;
                pos++;
            }
        }

        int words = (int)blockSize / 2;
        for (int g = 0; g < total; g++)
        {
            ct.ThrowIfCancellationRequested();
            int f = blockFile[g];
            int b = blockInFile[g];
            var block = new byte[blockSize];
            await ReadBlockZeroPaddedAsync(files[f], b, blockSize, block, ct);

            int baseValue = Galois16.BlockBase(g);
            for (int r = 0; r < recoveryBlockCount; r++)
            {
                int factor = Galois16.Pow(baseValue, firstRecoveryBlock + r);
                if (factor != 0)
                    ReedSolomon.AddMultiply(recovery[r], (int)blockSize, block, factor);
            }
        }
        return recovery;
    }

    private static async Task ReadBlockZeroPaddedAsync(string path, int blockNumber, long blockSize, byte[] dest, CancellationToken ct)
    {
        long offset = (long)blockNumber * blockSize;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (offset >= fs.Length)
        {
            Array.Clear(dest, 0, dest.Length);
            return;
        }
        fs.Position = offset;
        int read = await fs.ReadAsync(dest, ct);
        if (read < dest.Length)
            Array.Clear(dest, read, dest.Length - read);
    }

    private static string VolumeName(string baseName, int start, int count, List<(int Start, int Count)> allocations)
    {
        int maxExp = 1;
        int maxCount = 1;
        foreach (var a in allocations)
        {
            if (a.Start > maxExp) maxExp = a.Start;
            if (a.Count > maxCount) maxCount = a.Count;
        }
        int digitsLow = Math.Max(1, maxExp.ToString().Length);
        int digitsCount = Math.Max(1, maxCount.ToString().Length);
        return $"{baseName}.vol{start.ToString("D" + digitsLow)}+{count.ToString("D" + digitsCount)}.par2";
    }

    private static long ComputeDefaultBlockSize(long totalSize)
    {
        long blocksize = (totalSize + 1999) / 2000;
        blocksize = Math.Max(blocksize, 4);
        blocksize = (blocksize + 3) & ~3L;
        long minblocksize = (totalSize + Par2Format.MaxBlocksPerFile - 1) / Par2Format.MaxBlocksPerFile;
        minblocksize = Math.Max(minblocksize, 4);
        minblocksize = (minblocksize + 3) & ~3L;
        return Math.Max(blocksize, minblocksize);
    }

    private static async Task<(Md5Hash Full, Md5Hash K16, (Md5Hash Hash, uint Crc)[] Blocks)> ComputeFileInfoAsync(
        string path, long blockSize, int blockCount, CancellationToken ct)
    {
        var blocks = new (Md5Hash, uint)[blockCount];
        var fileCtx = new Md5Context();
        var k16Ctx = new Md5Context();
        bool k16Done = false;
        var blockCtx = new Md5Context();
        uint blockCrc = 0;
        int blockNumber = 0;
        long need = blockSize;
        long fileOffset = 0;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var buf = new byte[81920];
        int read;
        while ((read = await fs.ReadAsync(buf, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();

            fileCtx.Update(buf.AsSpan(0, read));
            if (!k16Done)
            {
                int take = Math.Min(read, (int)(16384 - fileOffset));
                k16Ctx.Update(buf.AsSpan(0, take));
                if (take == 16384 - fileOffset)
                    k16Done = true;
            }

            int used = 0;
            while (used < read)
            {
                int use = Math.Min((int)need, read - used);
                blockCrc = ~Crc32.Update(~blockCrc, buf.AsSpan(used, use));
                blockCtx.Update(buf.AsSpan(used, use));
                used += use;
                need -= use;
                if (need == 0)
                {
                    blocks[blockNumber] = (blockCtx.Final(), blockCrc);
                    blockNumber++;
                    if (blockNumber < blockCount)
                    {
                        need = blockSize;
                        blockCtx = new Md5Context();
                        blockCrc = 0;
                    }
                    else
                    {
                        need = long.MaxValue;
                        break;
                    }
                }
            }
            fileOffset += read;
        }

        if (need > 0 && need != long.MaxValue && blockNumber < blockCount)
        {
            blockCrc = ~Crc32.UpdateZeros(~blockCrc, (int)need);
            blockCtx.UpdateZeros((int)need);
            blocks[blockNumber] = (blockCtx.Final(), blockCrc);
            blockNumber++;
        }

        Md5Hash full = fileCtx.Final();
        Md5Hash k16 = k16Done ? k16Ctx.Final() : full;
        return (full, k16, blocks);
    }

    private static List<(int Start, int Count)> ComputeAllocations(
        int recoveryBlockCount, int recoveryFileCount, RecoveryFileScheme scheme, int firstBlock, long blockSize, long largestFileSize)
    {
        var result = new List<(int Start, int Count)>();
        int exponent = firstBlock;
        if (recoveryFileCount > 0)
        {
            switch (scheme)
            {
                case RecoveryFileScheme.Uniform:
                {
                    int baseCount = recoveryBlockCount / recoveryFileCount;
                    int remainder = recoveryBlockCount % recoveryFileCount;
                    for (int f = 0; f < recoveryFileCount; f++)
                    {
                        int count = (f < remainder) ? baseCount + 1 : baseCount;
                        result.Add((exponent, count));
                        exponent += count;
                    }
                    break;
                }
                case RecoveryFileScheme.Variable:
                {
                    int low = 1;
                    int max = (1 << recoveryFileCount) - 1;
                    while (max < recoveryBlockCount)
                    {
                        low <<= 1;
                        max <<= 1;
                    }
                    int blocks = recoveryBlockCount;
                    for (int f = 0; f < recoveryFileCount; f++)
                    {
                        int number = Math.Min(low, blocks);
                        result.Add((exponent, number));
                        exponent += number;
                        blocks -= number;
                        low <<= 1;
                    }
                    break;
                }
                case RecoveryFileScheme.Limited:
                {
                    int largest = (int)((largestFileSize + blockSize - 1) / blockSize);
                    int filenumber = recoveryFileCount;
                    int blocks = recoveryBlockCount;
                    int expTop = firstBlock + recoveryBlockCount;
                    while (blocks >= 2 * largest && filenumber > 0)
                    {
                        filenumber--;
                        expTop -= largest;
                        blocks -= largest;
                        result.Add((expTop, largest));
                    }
                    int count = 1;
                    for (int f = 0; f < filenumber; f++)
                    {
                        int number = Math.Min(count, blocks);
                        result.Add((exponent, number));
                        exponent += number;
                        blocks -= number;
                        count <<= 1;
                    }
                    break;
                }
            }
        }
        result.Add((exponent, 0));
        return result;
    }
}
