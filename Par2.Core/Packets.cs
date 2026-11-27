using System.Text;

namespace Par2.Core;

/// <summary>Constants for the PAR 2.0 packet format (all little-endian).</summary>
public static class Par2Format
{
    public static readonly byte[] Magic = new byte[] { (byte)'P', (byte)'A', (byte)'R', (byte)'2', 0, (byte)'P', (byte)'K', (byte)'T' };

    public const string TypeMain = "PAR 2.0\0Main";
    public const string TypeFileDesc = "PAR 2.0\0FileDesc";
    public const string TypeVerification = "PAR 2.0\0IFSC";
    public const string TypeRecovery = "PAR 2.0\0RecvSlic";
    public const string TypeCreator = "PAR 2.0\0Creator";

    public const int HeaderSize = 64;      // 8 magic + 8 length + 16 hash + 16 setid + 16 type
    public const int HashOffset = 16;      // header.hash
    public const int SetIdOffset = 32;     // header.setid
    public const int TypeOffset = 48;      // header.type
    public const int RecoveryExponentOffset = 64; // first body field of a recovery packet

    public const int MainBodySize = 8 + 4;             // blocksize + recoverablefilecount
    public const int FileDescBodyFixed = 16 * 3 + 8;   // fileid + hashfull + hash16k + length
    public const int VerifBodyFixed = 16;              // fileid
    public const int VerifEntrySize = 16 + 4;          // entry.hash + entry.crc
    public const int MaxBlocksPerFile = 32768;

    /// <summary>
    /// Compute the packet hash for a fully built packet: MD5 over
    /// <c>packet[SetIdOffset .. end)</c>.
    /// </summary>
    public static void FillPacketHash(byte[] packet)
    {
        var ctx = new Md5Context();
        ctx.Update(packet[SetIdOffset..]);
        var h = ctx.Final();
        Buffer.BlockCopy(h.Data, 0, packet, HashOffset, 16);
    }
}

/// <summary>The kinds of packets that make up a PAR 2.0 file.</summary>
public enum Par2PacketKind
{
    Main,
    FileDescription,
    FileVerification,
    Recovery,
    Creator,
    Unknown
}

/// <summary>
/// A single parsed PAR 2.0 packet. <see cref="Data"/> holds the complete
/// packet (header and body). Body accessors interpret the format.
/// </summary>
public sealed class Par2Packet
{
    public byte[] Data { get; }
    public Par2PacketKind Kind { get; }

    // Main packet
    public ulong BlockSize { get; private set; }
    public int RecoverableFileCount { get; private set; }
    public int TotalFileCount { get; private set; }
    public Md5Hash[] FileIds { get; private set; } = Array.Empty<Md5Hash>();

    // File description packet
    public Md5Hash FileId { get; private set; } = default!;
    public Md5Hash HashFull { get; private set; } = default!;
    public Md5Hash Hash16k { get; private set; } = default!;
    public ulong FileLength { get; private set; }
    public string FileName { get; private set; } = "";

    // File verification packet
    public int BlockCount { get; private set; }
    public (Md5Hash Hash, uint Crc)[] Entries { get; private set; } = Array.Empty<(Md5Hash, uint)>();

    // Recovery packet
    public int Exponent { get; private set; }

    private Par2Packet(byte[] data, Par2PacketKind kind)
    {
        Data = data;
        Kind = kind;
    }

    public ulong Length => BitConverter.ToUInt64(Data, 8);

    public Md5Hash PacketHash => new(Data[Par2Format.HashOffset..(Par2Format.HashOffset + 16)]);

    public Md5Hash SetId => new(Data[Par2Format.SetIdOffset..(Par2Format.SetIdOffset + 16)]);

    /// <summary>The data (body) portion of a recovery packet.</summary>
    public byte[] RecoveryData => Data[(Par2Format.RecoveryExponentOffset + 4)..];

    /// <summary>
    /// Parse a packet from a buffer that starts at the packet header.
    /// Returns null if the header does not look like a PAR 2 packet.
    /// </summary>
    public static Par2Packet? TryParse(ReadOnlySpan<byte> buffer, int offset, int available)
    {
        if (available < Par2Format.HeaderSize)
            return null;
        for (int i = 0; i < 8; i++)
            if (buffer[offset + i] != Par2Format.Magic[i])
                return null;

        ulong length = BitConverter.ToUInt64(buffer[(offset + 8)..]);
        if (length < Par2Format.HeaderSize || length > (ulong)available)
            return null;

        var kind = ParseType(buffer[(offset + Par2Format.TypeOffset)..(offset + Par2Format.TypeOffset + 16)]);
        var data = new byte[length];
        buffer[offset..(offset + (int)length)].CopyTo(data);

        var packet = new Par2Packet(data, kind);
        packet.ParseBody();
        return packet;
    }

    private static Par2PacketKind ParseType(ReadOnlySpan<byte> t)
    {
        string s = ReadString(t, 16);
        if (s == Par2Format.TypeMain) return Par2PacketKind.Main;
        if (s == Par2Format.TypeFileDesc) return Par2PacketKind.FileDescription;
        if (s == Par2Format.TypeVerification) return Par2PacketKind.FileVerification;
        if (s == Par2Format.TypeRecovery) return Par2PacketKind.Recovery;
        if (s == Par2Format.TypeCreator) return Par2PacketKind.Creator;
        return Par2PacketKind.Unknown;
    }

    private void ParseBody()
    {
        var d = Data;
        switch (Kind)
        {
            case Par2PacketKind.Main:
            {
                BlockSize = BitConverter.ToUInt64(d, 64);
                RecoverableFileCount = BitConverter.ToInt32(d, 72);
                TotalFileCount = RecoverableFileCount;
                int n = (d.Length - Par2Format.HeaderSize - Par2Format.MainBodySize) / 16;
                FileIds = new Md5Hash[n];
                for (int i = 0; i < n; i++)
                    FileIds[i] = new Md5Hash(d[(Par2Format.HeaderSize + Par2Format.MainBodySize + i * 16)..(Par2Format.HeaderSize + Par2Format.MainBodySize + i * 16 + 16)]);
                break;
            }
            case Par2PacketKind.FileDescription:
            {
                int b = Par2Format.HeaderSize;
                FileId = new Md5Hash(d[b..(b + 16)]); b += 16;
                HashFull = new Md5Hash(d[b..(b + 16)]); b += 16;
                Hash16k = new Md5Hash(d[b..(b + 16)]); b += 16;
                FileLength = BitConverter.ToUInt64(d, b); b += 8;
                FileName = ReadName(d, b);
                break;
            }
            case Par2PacketKind.FileVerification:
            {
                int b = Par2Format.HeaderSize;
                FileId = new Md5Hash(d[b..(b + 16)]); b += 16;
                BlockCount = (d.Length - Par2Format.HeaderSize - Par2Format.VerifBodyFixed) / Par2Format.VerifEntrySize;
                Entries = new (Md5Hash, uint)[BlockCount];
                for (int i = 0; i < BlockCount; i++)
                {
                    int e = b + i * Par2Format.VerifEntrySize;
                    var h = new Md5Hash(d[e..(e + 16)]);
                    uint crc = BitConverter.ToUInt32(d, e + 16);
                    Entries[i] = (h, crc);
                }
                break;
            }
            case Par2PacketKind.Recovery:
                Exponent = BitConverter.ToInt32(d, Par2Format.RecoveryExponentOffset);
                break;
        }
    }

    private static string ReadName(byte[] d, int start)
    {
        int end = d.Length;
        // The name is zero padded; find the first zero.
        int stop = start;
        while (stop < end && d[stop] != 0)
            stop++;
        return Encoding.UTF8.GetString(d, start, stop - start);
    }

    // The packet type field is a fixed 16-byte string that itself contains an
    // embedded NUL (e.g. "PAR 2.0\0Main"), so it must not be truncated at the
    // first NUL. Read all 16 bytes and strip only trailing NUL padding.
    private static string ReadString(ReadOnlySpan<byte> t, int max)
    {
        int len = Math.Min(max, t.Length);
        while (len > 0 && t[len - 1] == 0)
            len--;
        return Encoding.UTF8.GetString(t[..len]);
    }

    // ---- Packet builders (used by the writer) ----

    public static Par2Packet BuildMain(ulong blockSize, Md5Hash[] fileIds)
    {
        int body = Par2Format.MainBodySize + fileIds.Length * 16;
        var d = new byte[Par2Format.HeaderSize + body];
        WriteHeader(d, Par2Format.TypeMain);
        WriteUInt64(d, 64, blockSize);
        WriteInt32(d, 72, fileIds.Length);
        for (int i = 0; i < fileIds.Length; i++)
            Buffer.BlockCopy(fileIds[i].Data, 0, d, 76 + i * 16, 16);
        return new Par2Packet(d, Par2PacketKind.Main) { BlockSize = blockSize, RecoverableFileCount = fileIds.Length, TotalFileCount = fileIds.Length, FileIds = (Md5Hash[])fileIds.Clone() };
    }

    public static Par2Packet BuildFileDescription(Md5Hash hashFull, Md5Hash hash16k, ulong length, string fileName)
    {
        byte[] name = Encoding.UTF8.GetBytes(fileName);
        int paddedName = name.Length;
        while (paddedName % 4 != 0)
            paddedName++;

        // Body: fileid(16) hashfull(16) hash16k(16) length(8) name(paddedName)
        int body = Par2Format.FileDescBodyFixed + paddedName;
        var d = new byte[Par2Format.HeaderSize + body];
        WriteHeader(d, Par2Format.TypeFileDesc);
        int b = Par2Format.HeaderSize;          // fileid (filled by FinishFileDescription)
        b += 16;
        Buffer.BlockCopy(hashFull.Data, 0, d, b, 16); b += 16;   // hashfull
        Buffer.BlockCopy(hash16k.Data, 0, d, b, 16); b += 16;    // hash16k
        WriteUInt64(d, b, length); b += 8;                  // length
        Buffer.BlockCopy(name, 0, d, b, name.Length);        // name (zero padded)
        return new Par2Packet(d, Par2PacketKind.FileDescription) { HashFull = hashFull, Hash16k = hash16k, FileLength = length, FileName = fileName };
    }

    /// <summary>
    /// Compute and store the fileid for a file description packet. fileid is
    /// the MD5 over hash16k, length, and the (unpadded) name.
    /// </summary>
    public static void FinishFileDescription(Par2Packet packet)
    {
        var d = packet.Data;
        int nameStart = Par2Format.HeaderSize + Par2Format.FileDescBodyFixed;
        int nameLen = d.Length - nameStart;
        while (nameLen > 0 && d[nameStart + nameLen - 1] == 0)
            nameLen--;
        int hash16kStart = Par2Format.HeaderSize + 32; // after fileid + hashfull
        var ctx = new Md5Context();
        ctx.Update(d[hash16kStart..nameStart]);            // hash16k + length
        ctx.Update(d[nameStart..(nameStart + nameLen)]);      // name without padding
        var fileid = ctx.Final();
        Buffer.BlockCopy(fileid.Data, 0, d, Par2Format.HeaderSize, 16);
        packet.FileId = fileid;
    }

    public static Par2Packet BuildVerification(Md5Hash fileid, (Md5Hash Hash, uint Crc)[] entries)
    {
        int body = Par2Format.VerifBodyFixed + entries.Length * Par2Format.VerifEntrySize;
        var d = new byte[Par2Format.HeaderSize + body];
        WriteHeader(d, Par2Format.TypeVerification);
        Buffer.BlockCopy(fileid.Data, 0, d, Par2Format.HeaderSize, 16);
        for (int i = 0; i < entries.Length; i++)
        {
            int e = Par2Format.HeaderSize + Par2Format.VerifBodyFixed + i * Par2Format.VerifEntrySize;
            Buffer.BlockCopy(entries[i].Hash.Data, 0, d, e, 16);
            WriteUInt32(d, e + 16, entries[i].Crc);
        }
        return new Par2Packet(d, Par2PacketKind.FileVerification) { FileId = fileid, BlockCount = entries.Length, Entries = entries };
    }

    public static Par2Packet BuildRecovery(int exponent, int blockSize)
    {
        int body = 4 + blockSize;
        var d = new byte[Par2Format.HeaderSize + body];
        WriteHeader(d, Par2Format.TypeRecovery);
        WriteUInt32(d, Par2Format.RecoveryExponentOffset, (uint)exponent);
        return new Par2Packet(d, Par2PacketKind.Recovery) { Exponent = exponent };
    }

    public static Par2Packet BuildCreator(string creator)
    {
        byte[] name = Encoding.UTF8.GetBytes(creator);
        int padded = name.Length;
        while (padded % 4 != 0)
            padded++;
        var d = new byte[Par2Format.HeaderSize + padded];
        WriteHeader(d, Par2Format.TypeCreator);
        Buffer.BlockCopy(name, 0, d, Par2Format.HeaderSize, name.Length);
        return new Par2Packet(d, Par2PacketKind.Creator);
    }

    /// <summary>Set the set id and packet hash of a built packet.</summary>
    public void Finish(Md5Hash setId)
    {
        Buffer.BlockCopy(setId.Data, 0, Data, Par2Format.SetIdOffset, 16);
        var ctx = new Md5Context();
        ctx.Update(Data[Par2Format.SetIdOffset..]);
        var h = ctx.Final();
        Buffer.BlockCopy(h.Data, 0, Data, Par2Format.HashOffset, 16);
    }

    private static void WriteHeader(byte[] d, string type)
    {
        Buffer.BlockCopy(Par2Format.Magic, 0, d, 0, 8);
        WriteUInt64(d, 8, (ulong)d.Length);
        // hash and setid filled later
        byte[] t = Encoding.UTF8.GetBytes(type);
        Buffer.BlockCopy(t, 0, d, Par2Format.TypeOffset, Math.Min(16, t.Length));
    }

    private static void WriteUInt64(byte[] d, int off, ulong v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, d, off, 8);
    private static void WriteUInt32(byte[] d, int off, uint v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, d, off, 4);
    private static void WriteInt32(byte[] d, int off, int v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, d, off, 4);
}
