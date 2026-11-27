namespace Par2.Core;

public enum RepairStatus
{
    Ok,                 // everything verified
    Repaired,           // damaged blocks were reconstructed
    NotEnoughRecovery,  // not enough recovery blocks to repair
    NotRepairable,      // damage beyond what recovery can fix
    NoFiles,            // no source files found
    Error
}

public sealed record FileReport(string Name, string? Path, int BlockCount, int MissingBlocks, string Problem);

public sealed record RepairReport(
    RepairStatus Status,
    int SourceFileCount,
    int SourceBlockCount,
    int MissingBlocks,
    int RecoveryBlocksAvailable,
    IReadOnlyList<FileReport> Files,
    string Message);

/// <summary>
/// Verifies and repairs a PAR 2.0 recovery set by checking the source files
/// in <c>baseDirectory</c> against the set's verification packets, and
/// reconstructing any damaged or missing blocks.
///
/// A source file is considered intact if its full-file MD5 matches the
/// description packet's <c>hashfull</c>. Otherwise each block's hash and CRC
/// are checked against the verification packet; blocks that do not match are
/// marked missing and are reconstructed with Reed-Solomon.
/// </summary>
public sealed class Par2Repairer
{
    private readonly Action<string> _log;
    private readonly bool _quiet;

    public Par2Repairer(Action<string>? log = null, bool quiet = false)
    {
        _log = log ?? (_ => { });
        _quiet = quiet;
    }

    private void Log(string message)
    {
        if (!_quiet)
            _log(message);
    }

    public async Task<RepairReport> RunAsync(RecoverySet set, string baseDirectory, bool repair, CancellationToken ct = default)
    {
        if (!set.HasMain)
            return new RepairReport(RepairStatus.Error, 0, 0, 0, set.Recovery.Count, Array.Empty<FileReport>(), "No main packet found for this set.");

        long blockSize = (long)set.BlockSize;
        var fileIds = set.Main.FileIds;

        // Candidate data files (everything in baseDirectory that is not a par2).
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<string>>();
        if (Directory.Exists(baseDirectory))
        {
            foreach (var f in Directory.EnumerateFiles(baseDirectory))
            {
                if (f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(f);
                if (!info.Exists)
                    continue;
                byName[Path.GetFileName(f)] = f;
                if (!bySize.TryGetValue(info.Length, out var list))
                    bySize[info.Length] = list = new List<string>();
                list.Add(f);
            }
        }

        var reports = new List<FileReport>();
        var perFile = new (int Blocks, string? Path, bool Full, bool[] Present)[fileIds.Length];
        int totalBlocks = 0;

        // ---- Match each description to a file and determine block presence ----
        for (int fi = 0; fi < fileIds.Length; fi++)
        {
            ct.ThrowIfCancellationRequested();
            var desc = set.Descriptions.FirstOrDefault(d => fileIds[fi].Equals(d.FileId));
            if (desc == null)
            {
                perFile[fi] = (0, null, false, Array.Empty<bool>());
                continue;
            }
            var verif = set.Verifications.FirstOrDefault(v => desc.FileId.Equals(v.FileId));
            var matched = await MatchAndVerifyAsync(desc, verif, byName, bySize, blockSize, ct);
            perFile[fi] = matched;
            int missing = matched.Present.Count(p => !p);
            reports.Add(new FileReport(desc.FileName, matched.Path, matched.Blocks, missing, DescribeProblem(matched)));
            totalBlocks += matched.Blocks;
        }

        if (totalBlocks == 0)
            return new RepairReport(RepairStatus.NoFiles, fileIds.Length, 0, 0, set.Recovery.Count, reports, "No source files could be associated with this set.");

        // Build the global arrays in main.FileIds order.
        var globalPresent = new bool[totalBlocks];
        var blockFile = new int[totalBlocks];
        var blockInFile = new int[totalBlocks];
        int pos = 0;
        for (int fi = 0; fi < fileIds.Length; fi++)
        {
            for (int b = 0; b < perFile[fi].Blocks; b++)
            {
                globalPresent[pos] = perFile[fi].Present[b];
                blockFile[pos] = fi;
                blockInFile[pos] = b;
                pos++;
            }
        }

        int missingCount = globalPresent.Count(p => !p);
        int available = set.Recovery.Count;

        Log($"You have {totalBlocks - missingCount} out of {totalBlocks} source blocks.");
        if (missingCount > 0)
        {
            Log($"You are missing {missingCount} source block(s).");
            if (available < missingCount)
            {
                int need = missingCount - available;
                Log($"You need {need} more recovery block(s) to repair this set.");
                return new RepairReport(RepairStatus.NotEnoughRecovery, fileIds.Length, totalBlocks, missingCount, available, reports,
                    $"Need {need} more recovery block(s).");
            }
        }

        if (missingCount == 0)
        {
            Log("All source files are intact.");
            return new RepairReport(RepairStatus.Ok, fileIds.Length, totalBlocks, 0, available, reports, "All source files verified OK.");
        }

        if (!repair)
        {
            Log($"{missingCount} source block(s) need repair. Run repair to reconstruct them.");
            return new RepairReport(RepairStatus.NotRepairable, fileIds.Length, totalBlocks, missingCount, available, reports,
                $"{missingCount} block(s) damaged; run repair to fix.");
        }

        // ---- Choose the lowest-missingCount recovery exponents ----
        var chosen = set.Recovery.Keys.OrderBy(e => e).Take(missingCount).ToArray();

        // ---- Build the RS matrix ----
        Log("Computing Reed Solomon matrix.");
        var rs = new ReedSolomon();
        rs.SetInput(globalPresent);
        foreach (int e in chosen)
            rs.SetOutput(true, e);

        if (!rs.Compute(out string? error))
            return new RepairReport(RepairStatus.NotRepairable, fileIds.Length, totalBlocks, missingCount, available, reports, error ?? "Reed Solomon failure.");

        int presentData = globalPresent.Count(p => p);

        // ---- Allocate output buffers ----
        var outputs = new byte[missingCount][];
        for (int r = 0; r < missingCount; r++)
            outputs[r] = new byte[blockSize];

        // ---- Input order: present data (global order) then chosen recovery ----
        var presentGlobal = new List<int>();
        for (int g = 0; g < totalBlocks; g++)
            if (globalPresent[g])
                presentGlobal.Add(g);

        Log("Repairing source blocks.");
        var inputBuf = new byte[blockSize];
        int inputIndex = 0;

        foreach (int g in presentGlobal)
        {
            ct.ThrowIfCancellationRequested();
            int fi = blockFile[g];
            int b = blockInFile[g];
            var (pfBlocks, pfPath, pfFull, pfPresent) = perFile[fi];
            if (pfPath == null)
                continue;
            await ReadBlockZeroPaddedAsync(pfPath, b, blockSize, inputBuf, ct);
            rs.Process((int)blockSize, inputIndex, inputBuf, outputs);
            inputIndex++;
        }

        foreach (int e in chosen)
        {
            ct.ThrowIfCancellationRequested();
            var recData = set.Recovery[e];
            Buffer.BlockCopy(recData, 0, inputBuf, 0, Math.Min(recData.Length, (int)blockSize));
            rs.Process((int)blockSize, inputIndex, inputBuf, outputs);
            inputIndex++;
        }

        // ---- Write the reconstructed blocks back ----
        Log("Writing recovered data.");
        var missingGlobals = new List<int>();
        for (int g = 0; g < totalBlocks; g++)
            if (!globalPresent[g])
                missingGlobals.Add(g);

        long totalWritten = 0;
        for (int r = 0; r < missingCount; r++)
        {
            ct.ThrowIfCancellationRequested();
            int g = missingGlobals[r];
            int fi = blockFile[g];
            int b = blockInFile[g];
            var (pfBlocks, pfPath, pfFull, pfPresent) = perFile[fi];
            if (pfPath == null)
                continue;
            var desc = set.Descriptions.FirstOrDefault(d => fileIds[fi].Equals(d.FileId));
            long offset = (long)b * blockSize;
            long writeLen = (long)outputs[r].Length;
            if (desc != null && offset + writeLen > (long)desc.FileLength)
                writeLen = (long)desc.FileLength - offset;
            if (writeLen <= 0)
                continue;
            await WriteBlockAsync(pfPath, offset, outputs[r], writeLen, ct);
            totalWritten += writeLen;
        }

        Log($"Wrote {totalWritten} bytes to disk.");
        return new RepairReport(RepairStatus.Repaired, fileIds.Length, totalBlocks, missingCount, available, reports,
            $"Repaired {missingCount} block(s).");
    }

    private static string DescribeProblem((int Blocks, string? Path, bool Full, bool[] Present) f)
    {
        if (f.Path == null)
            return "not found";
        if (f.Full)
            return "ok";
        int missing = f.Present.Count(p => !p);
        return missing == 0 ? "ok" : $"damaged ({missing} block(s))";
    }

    // ---- Matching and per-block verification ----

    private async Task<(int Blocks, string? Path, bool Full, bool[] Present)> MatchAndVerifyAsync(
        Par2Packet desc, Par2Packet? verif, Dictionary<string, string> byName,
        Dictionary<long, List<string>> bySize, long blockSize, CancellationToken ct)
    {
        int blocks = (int)(((long)desc.FileLength + blockSize - 1) / blockSize);
        string name = desc.FileName;

        string? candidate = null;
        if (byName.TryGetValue(name, out var named))
            candidate = named;
        else if (bySize.TryGetValue((long)desc.FileLength, out var sized) && sized.Count > 0)
        {
            // Same size: check full hash to confirm it is the right file.
            foreach (var f in sized)
            {
                ct.ThrowIfCancellationRequested();
                var h = await Md5OfAsync(f, ct);
                if (desc.HashFull.Equals(h))
                {
                    candidate = f;
                    break;
                }
            }
        }

        if (candidate == null)
            return (blocks, null, false, new bool[blocks]);

        var fullHash = await Md5OfAsync(candidate, ct);
        bool fullMatch = desc.HashFull.Equals(fullHash);
        if (fullMatch)
        {
            return (blocks, candidate, true, Enumerable.Repeat(true, blocks).ToArray());
        }

        // Damaged: verify block by block against the verification packet.
        var present = new bool[blocks];
        if (verif == null)
        {
            Array.Clear(present, 0, blocks);
            return (blocks, candidate, false, present);
        }

        var buf = new byte[blockSize];
        for (int b = 0; b < blocks; b++)
        {
            ct.ThrowIfCancellationRequested();
            long offset = (long)b * blockSize;
            if (offset >= (long)desc.FileLength)
                break;
            await ReadBlockZeroPaddedAsync(candidate, b, blockSize, buf, ct);
            var h = Md5Context.HashBytes(buf);
            uint crc = Crc32.BlockCrc(buf, (int)blockSize);
            present[b] = b < verif.BlockCount && verif.Entries[b].Hash.Equals(h) && verif.Entries[b].Crc == crc;
        }
        return (blocks, candidate, false, present);
    }

    private static Task<Md5Hash> Md5OfAsync(string path, CancellationToken ct)
    {
        return Task.Run(() => Md5Context.HashFile(path), ct);
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

    private static async Task WriteBlockAsync(string path, long offset, byte[] data, long writeLen, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 81920, useAsync: true);
        if (fs.Length < offset + writeLen)
            fs.SetLength(offset + writeLen);
        fs.Position = offset;
        await fs.WriteAsync(data.AsMemory(0, (int)writeLen), ct);
    }
}
