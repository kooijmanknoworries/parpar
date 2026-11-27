namespace Par2.Core;

/// <summary>
/// A single PAR 2.0 recovery set: the aggregated critical packets and
/// recovery packets for one set id, collected from every .par2 file that
/// belongs to it (the main .par2 plus the volume files).
/// </summary>
public sealed class RecoverySet
{
    public string SetId { get; private set; } = "";
    public ulong BlockSize { get; private set; }
    public Par2Packet Main { get; private set; }
    public List<Par2Packet> Descriptions { get; } = new();
    public List<Par2Packet> Verifications { get; } = new();
    public List<Par2Packet> Creators { get; } = new();
    public Dictionary<int, byte[]> Recovery { get; } = new(); // exponent -> block data
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

    public void AddRecovery(Par2Packet rec)
    {
        if (!Recovery.ContainsKey(rec.Exponent))
        {
            Recovery[rec.Exponent] = rec.RecoveryData;
        }
    }
}

/// <summary>
/// Scans a directory for PAR 2.0 files, validates each packet, and groups
/// the packets into recovery sets by set id. Damaged packets (bad hash) are
/// skipped, so partially damaged volume files still contribute their intact
/// recovery blocks.
/// </summary>
public static class Par2Scanner
{
    public static List<RecoverySet> ScanDirectory(string directory)
    {
        var sets = new Dictionary<string, RecoverySet>();
        if (!Directory.Exists(directory))
            return new List<RecoverySet>();

        string[] parFiles;
        try
        {
            parFiles = Directory.EnumerateFiles(directory, "*.par2", SearchOption.TopDirectoryOnly)
                                  .Where(f => !f.EndsWith(".check.par2", StringComparison.OrdinalIgnoreCase))
                                  .ToArray();
        }
        catch
        {
            return new List<RecoverySet>();
        }

        foreach (string file in parFiles)
        {
            ScanFile(file, sets);
        }

        return sets.Values
                   .Where(s => s.HasMain || s.Recovery.Count > 0)
                   .ToList();
    }

    private static void ScanFile(string path, Dictionary<string, RecoverySet> sets)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch
        {
            return;
        }

        int offset = 0;
        int setIndex = -1;
        string setId = "";

        while (offset + Par2Format.HeaderSize <= data.Length)
        {
            var packet = Par2Packet.TryParse(data, offset, data.Length - offset);
            if (packet == null)
                break; // unrecognised / damaged: stop scanning this file

            // Verify the packet hash (covers setid, type, and body).
            var recomputed = new Md5Context();
            recomputed.Update(data, offset + Par2Format.SetIdOffset, (int)packet.Length - Par2Format.SetIdOffset);
            var expected = recomputed.Final();
            if (!expected.Equals(packet.PacketHash))
                break; // damaged packet: stop (rest of this file is suspect)

            offset += (int)packet.Length;

            // Determine the set this file belongs to.
            if (packet.SetId.IsZero)
                continue;
            if (setId == "")
            {
                setId = packet.SetId.ToString();
                setIndex = 0;
            }

            if (!sets.TryGetValue(setId, out var set))
            {
                set = new RecoverySet();
                set.Files.Add(path);
                sets[setId] = set;
            }

            switch (packet.Kind)
            {
                case Par2PacketKind.Main:
                    set.AddMain(packet);
                    break;
                case Par2PacketKind.FileDescription:
                    if (!set.Descriptions.Any(d => packet.FileId.Equals(d.FileId)))
                        set.Descriptions.Add(packet);
                    break;
                case Par2PacketKind.FileVerification:
                    if (!set.Verifications.Any(v => packet.FileId.Equals(v.FileId)))
                    {
                        set.Verifications.Add(packet);
                        if (set.HasMain)
                        {
                            set.SourceBlockCount = 0;
                            foreach (var fileId in set.Main.FileIds)
                            {
                                var v = set.Verifications.FirstOrDefault(x => fileId.Equals(x.FileId));
                                if (v != null)
                                    set.SourceBlockCount += v.BlockCount;
                            }
                        }
                    }
                    break;
                case Par2PacketKind.Recovery:
                    set.AddRecovery(packet);
                    break;
                case Par2PacketKind.Creator:
                    if (set.Creators.Count == 0)
                        set.Creators.Add(packet);
                    break;
            }
        }

        // Recompute the expected recovery block count = highest exponent + 1.
        foreach (var set in sets.Values)
        {
            if (set.Recovery.Count > 0)
                set.RecoveryBlockCount = set.Recovery.Max(kv => kv.Key) + 1;
        }
    }
}
