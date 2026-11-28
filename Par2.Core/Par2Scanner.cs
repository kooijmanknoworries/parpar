namespace Par2.Core;

/// <summary>
/// Where the recovery data of a block lives. The bytes are deliberately NOT
/// loaded at scan time: a watched directory may hold many large sets, and
/// keeping every recovery block in memory would use gigabytes and thrash the
/// disk on every scan. The bytes are read on demand, only for the blocks a
/// repair actually needs.
/// </summary>
public sealed record RecoveryBlockRef(int Exponent, string File, long Offset, int Length)
{
    /// <summary>Read the block data of this recovery block from disk.</summary>
    public byte[] ReadData()
    {
        try
        {
            byte[] data = new byte[Length];
            using var fs = new FileStream(File, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: false);
            fs.Position = Offset;
            int read = 0;
            while (read < Length)
            {
                int n = fs.Read(data, read, Length - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read < Length)
                return Array.Empty<byte>(); // file shrank between scan and use
            return data;
        }
        catch
        {
            return Array.Empty<byte>(); // file went away between scan and use
        }
    }
}

/// <summary>
/// A single PAR 2.0 recovery set: the aggregated critical packets and
/// recovery blocks for one set id, collected from every .par2 file that
/// belongs to it (the main .par2 plus the volume files).
/// </summary>
public sealed class RecoverySet
{
    public string SetId { get; private set; } = "";
    public ulong BlockSize { get; private set; }
    public Par2Packet? Main { get; private set; }
    public List<Par2Packet> Descriptions { get; } = new();
    public List<Par2Packet> Verifications { get; } = new();
    public List<Par2Packet> Creators { get; } = new();
    public Dictionary<int, RecoveryBlockRef> Recovery { get; } = new(); // exponent -> location
    public List<string> Files { get; } = new();              // .par2 paths that contributed

    public int SourceBlockCount { get; set; }
    public int RecoveryBlockCount { get; set; }

    /// <summary>True if a valid main packet has been read.</summary>
    public bool HasMain => Main != null;

    public void AddMain(Par2Packet main)
    {
        if (Main != null)
            return;
        Main = main;
        SetId = main.SetId.ToString();
        BlockSize = main.BlockSize;
        SourceBlockCount = 0;
        foreach (var fileId in main.FileIds)
        {
            var v = Verifications.FirstOrDefault(x => fileId.Equals(x.FileId));
            if (v != null)
                SourceBlockCount += v.BlockCount;
        }
    }

    public void AddRecoveryBlock(RecoveryBlockRef block)
    {
        if (!Recovery.ContainsKey(block.Exponent))
            Recovery[block.Exponent] = block;
    }
}

/// <summary>
/// Scans a directory for PAR 2.0 files, validates each packet, and groups
/// the packets into recovery sets by set id.
///
/// Files are streamed in small chunks, so scanning a directory with many
/// large recovery volumes uses only a few megabytes of memory, no matter how
/// much recovery data is on disk. Damaged packets (bad hash) end the scan of
/// their file, so partially damaged volume files still contribute their
/// intact recovery blocks. Files that cannot be read as PAR 2.0 data at all
/// are reported per file with a reason, so a watched directory never sits
/// completely silent.
/// </summary>
public static class Par2Scanner
{
    /// <summary>One .par2 file that could not be used, with the reason.</summary>
    public sealed record UnreadableFile(string Path, string Reason);

    /// <summary>Result of a directory scan: the usable sets and the skipped files.</summary>
    public sealed record ScanResult(List<RecoverySet> Sets, List<UnreadableFile> Unreadable);

    public static ScanResult ScanDirectory(string directory)
    {
        var sets = new Dictionary<string, RecoverySet>();
        var unreadable = new List<UnreadableFile>();
        if (!Directory.Exists(directory))
            return new ScanResult(new List<RecoverySet>(), unreadable);

        string[] parFiles;
        try
        {
            parFiles = Directory.EnumerateFiles(directory, "*.par2", SearchOption.TopDirectoryOnly)
                                  .Where(f => !f.EndsWith(".check.par2", StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                  .ToArray();
        }
        catch
        {
            return new ScanResult(new List<RecoverySet>(), unreadable);
        }

        foreach (string file in parFiles)
            ScanFile(file, sets, unreadable);

        // Expected recovery block count = highest recovery exponent seen + 1.
        foreach (var set in sets.Values)
            if (set.Recovery.Count > 0)
                set.RecoveryBlockCount = set.Recovery.Max(kv => kv.Key) + 1;

        return new ScanResult(
            sets.Values.Where(s => s.HasMain || s.Recovery.Count > 0).ToList(),
            unreadable);
    }

    /// <summary>
    /// Stream one .par2 file and fold its packets into <paramref name="sets"/>.
    /// Keeps only metadata and recovery block locations, never the block bytes.
    /// </summary>
    private static void ScanFile(string path, Dictionary<string, RecoverySet> sets, List<UnreadableFile> unreadable)
    {
        FileStream? fs;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: false);
        }
        catch (Exception e)
        {
            unreadable.Add(new UnreadableFile(path, $"could not open: {e.Message}"));
            return;
        }
        try
        {
            ScanFileCore(fs, path, sets, unreadable);
        }
        finally
        {
            fs.Dispose();
        }
    }

    private static void ScanFileCore(FileStream fs, string path, Dictionary<string, RecoverySet> sets, List<UnreadableFile> unreadable)
    {

        // The first packet must start at offset 0 and carry the standard PAR 2
        // magic. Anything else (a plain archive, a still-empty download, a
        // partially flushed file) is reported and skipped.
        long length = fs.Length;
        if (length < Par2Format.HeaderSize)
        {
            unreadable.Add(new UnreadableFile(path, $"not a PAR 2 file ({length} bytes)"));
            return;
        }

        string setId = "";
        long offset = 0;
        while (offset + Par2Format.HeaderSize <= fs.Length)
        {
            var header = new byte[Par2Format.HeaderSize];
            if (!ReadExact(fs, header, header.Length))
            {
                unreadable.Add(new UnreadableFile(path, $"stopped at offset {offset}: file changed"));
                return;
            }
            if (!Par2Packet.TryParseHeader(header, (ulong)(fs.Length - offset), out var kind, out ulong packetLength))
            {
                bool magicOk = true;
                for (int i = 0; i < 8; i++)
                    if (header[i] != Par2Format.Magic[i]) { magicOk = false; break; }
                unreadable.Add(new UnreadableFile(path, magicOk
                    ? $"stopped at offset {offset}: damaged or truncated packet"
                    : $"stopped at offset {offset}: packet not recognisable"));
                return;
            }

            // Verify the packet hash (covers setid, type, and body) while
            // walking the packet. For recovery packets the (large) body is
            // hashed in chunks and skipped, so it is never copied.
            var ctx = new Md5Context();
            ctx.Update(header.AsSpan(Par2Format.SetIdOffset, header.Length - Par2Format.SetIdOffset));
            long body = (long)packetLength - Par2Format.HeaderSize;
            RecoveryBlockRef? blockRef = null;
            Par2Packet? packet = null;

            if (kind == Par2PacketKind.Recovery)
            {
                var exp4 = new byte[4];
                if (body < 4 || !ReadExact(fs, exp4, 4))
                {
                    unreadable.Add(new UnreadableFile(path, $"stopped at offset {offset}: truncated recovery packet"));
                    return;
                }
                int exponent = BitConverter.ToInt32(exp4, 0);
                if (exponent < 0)
                {
                    unreadable.Add(new UnreadableFile(path, $"stopped at offset {offset}: invalid recovery exponent"));
                    return;
                }
                ctx.Update(exp4);
                if (!HashAndSkip(fs, ctx, body - 4))
                {
                    unreadable.Add(new UnreadableFile(path, $"stopped at offset {offset}: truncated recovery packet"));
                    return;
                }
                blockRef = new RecoveryBlockRef(exponent, path, offset + Par2Format.HeaderSize + 4, (int)(packetLength - Par2Format.HeaderSize - 4));
            }
            else
            {
                // Critical packets (main, description, verification, creator)
                // are small: read the body to finish the hash, then parse the
                // complete packet.
                var rest = new byte[(int)body];
                if (!ReadExact(fs, rest, rest.Length))
                {
                    unreadable.Add(new UnreadableFile(path, $"stopped at offset {offset}: truncated packet"));
                    return;
                }
                ctx.Update(rest);
                var full = new byte[(int)packetLength];
                Buffer.BlockCopy(header, 0, full, 0, Par2Format.HeaderSize);
                Buffer.BlockCopy(rest, 0, full, Par2Format.HeaderSize, (int)body);
                packet = Par2Packet.TryParse(full, 0, full.Length);
                if (packet == null)
                {
                    unreadable.Add(new UnreadableFile(path, $"stopped at offset {offset}: damaged packet"));
                    return;
                }
            }

            var expected = ctx.Final();
            var stored = new Md5Hash(header[Par2Format.HashOffset..(Par2Format.HashOffset + 16)]);
            if (!expected.Equals(stored))
            {
                unreadable.Add(new UnreadableFile(path, $"stopped at offset {offset}: packet hash mismatch (damaged)"));
                return;
            }

            offset += (int)packetLength;

            var packetSetId = new Md5Hash(header[Par2Format.SetIdOffset..(Par2Format.SetIdOffset + 16)]);
            if (packetSetId.IsZero)
                continue;

            switch (kind)
            {
                case Par2PacketKind.Recovery:
                    AddToSet(sets, ref setId, packetSetId, path, s => s.AddRecoveryBlock(blockRef!));
                    break;
                case Par2PacketKind.Main:
                    AddToSet(sets, ref setId, packetSetId, path, s => s.AddMain(packet!));
                    break;
                case Par2PacketKind.FileDescription:
                    AddToSet(sets, ref setId, packetSetId, path, s =>
                    {
                        var pd = packet!;
                        if (!s.Descriptions.Any(d => pd.FileId.Equals(d.FileId)))
                            s.Descriptions.Add(pd);
                    });
                    break;
                case Par2PacketKind.FileVerification:
                    AddToSet(sets, ref setId, packetSetId, path, s =>
                    {
                        var pv = packet!;
                        if (!s.Verifications.Any(v => pv.FileId.Equals(v.FileId)))
                        {
                            s.Verifications.Add(pv);
                            if (s.Main is { } main)
                            {
                                s.SourceBlockCount = 0;
                                foreach (var fileId in main.FileIds)
                                {
                                    var v = s.Verifications.FirstOrDefault(x => fileId.Equals(x.FileId));
                                    if (v != null)
                                        s.SourceBlockCount += v.BlockCount;
                                }
                            }
                        }
                    });
                    break;
                case Par2PacketKind.Creator:
                    AddToSet(sets, ref setId, packetSetId, path, s =>
                    {
                        var pc = packet!;
                        if (s.Creators.Count == 0)
                            s.Creators.Add(pc);
                    });
                    break;
            }
        }
    }

    private static void AddToSet(Dictionary<string, RecoverySet> sets, ref string setId, Md5Hash packetSetId, string path, Action<RecoverySet> fold)
    {
        if (setId == "")
            setId = packetSetId.ToString();
        if (!sets.TryGetValue(setId, out var set))
        {
            set = new RecoverySet();
            sets[setId] = set;
        }
        if (!set.Files.Contains(path))
            set.Files.Add(path);
        fold(set);
    }

    private static bool HashAndSkip(FileStream fs, Md5Context ctx, long remaining)
    {
        var chunk = new byte[81920];
        while (remaining > 0)
        {
            int n = (int)Math.Min(chunk.Length, remaining);
            if (!ReadExact(fs, chunk, n))
                return false;
            ctx.Update(chunk, 0, n);
            remaining -= n;
        }
        return true;
    }

    private static bool ReadExact(FileStream fs, byte[] dest, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = fs.Read(dest, read, count - read);
            if (n <= 0)
                return false;
            read += n;
        }
        return true;
    }
}
