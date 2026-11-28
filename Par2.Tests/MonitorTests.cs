using Par2.Core;
using Xunit;

namespace Par2.Tests;

public class MonitorTests : IDisposable
{
    private readonly string _dir;

    public MonitorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "parpar_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void MakeFile(string dir, string name, int bytes, int seed)
    {
        var data = new byte[bytes];
        for (int i = 0; i < bytes; i++)
            data[i] = (byte)((i * 31 + seed) & 0xFF);
        File.WriteAllBytes(Path.Combine(dir, name), data);
    }

    private static byte[] WithDamagedBlocks(byte[] original, int blockSize, params int[] blockIndexes)
    {
        var damaged = (byte[])original.Clone();
        foreach (int b in blockIndexes)
        {
            int pos = b * blockSize + blockSize / 2;
            if (pos < damaged.Length)
            {
                damaged[pos] ^= 0xFF;
                damaged[(pos + 1) % damaged.Length] ^= 0x55;
            }
        }
        return damaged;
    }

    [Fact]
    public async Task RepairsSetOnlyWhenAtLeastAsManyRecoveryBlocksAsMissingBlocks()
    {
        // Two blocks damaged; with only 1 of 8 recovery blocks present the set
        // cannot be repaired yet, so the monitor waits. Once the rest arrive it
        // repairs.
        string dataName = "data.bin";
        MakeFile(_dir, dataName, 2000, 1);
        var dataFile = Path.Combine(_dir, dataName);
        var dataBackup = File.ReadAllBytes(dataFile);

        var opt = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 8, RecoveryFileCount = 8, Quiet = true, Log = _ => { } };
        await Par2Writer.CreateAsync(opt, Path.Combine(_dir, "set.par2"), new[] { dataFile });

        var allPar2 = Directory.GetFiles(_dir, "*.par2").OrderBy(x => x).ToList();
        var hide = allPar2.Where(f => f != Path.Combine(_dir, "set.par2")).ToList();
        // Keep only the first recovery volume, hide the other seven.
        foreach (var f in hide.Skip(1))
            File.Move(f, f + ".hidden");

        // Damage two distinct blocks so two recovery blocks are required.
        File.WriteAllBytes(dataFile, WithDamagedBlocks(dataBackup, 64, 3, 17));

        var log = new List<string>();
        var mon = new Par2Monitor(_dir, new MonitorOptions { Log = log.Add });
        await mon.RunOnceAsync();

        var afterFirst = File.ReadAllBytes(dataFile);
        Assert.NotEqual(dataBackup, afterFirst);             // still damaged
        Assert.DoesNotContain(log, l => l.Contains("repaired"));
        Assert.Contains(log, l => l.Contains("not enough recovery blocks"));

        // Reveal all recovery volumes.
        foreach (var f in hide.Skip(1))
            File.Move(f + ".hidden", f);

        await mon.RunOnceAsync();

        Assert.Equal(dataBackup, File.ReadAllBytes(dataFile));
        Assert.Contains(log, l => l.Contains("repaired"));
    }

    [Fact]
    public async Task MovesOnToNextSetWhenOneNeedsMoreRecovery()
    {
        // Set A is damaged but short on recovery; set B is damaged and has all
        // its recovery. The monitor must repair B in the same pass and leave A
        // for later — it never blocks on A.
        var optA = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 4, RecoveryFileCount = 4, Quiet = true, Log = _ => { } };
        var optB = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 4, RecoveryFileCount = 4, Quiet = true, Log = _ => { } };

        MakeFile(_dir, "a.bin", 1200, 5);
        MakeFile(_dir, "b.bin", 1200, 6);
        var aFile = Path.Combine(_dir, "a.bin");
        var bFile = Path.Combine(_dir, "b.bin");
        var aBackup = File.ReadAllBytes(aFile);
        var bBackup = File.ReadAllBytes(bFile);

        await Par2Writer.CreateAsync(optA, Path.Combine(_dir, "a.par2"), new[] { aFile });
        await Par2Writer.CreateAsync(optB, Path.Combine(_dir, "b.par2"), new[] { bFile });

        // Set A: damage 3 blocks but keep only 2 of its 4 recovery volumes.
        File.WriteAllBytes(aFile, WithDamagedBlocks(aBackup, 64, 2, 9, 15));
        var aVolumes = Directory.GetFiles(_dir, "*.par2")
            .Where(f => !f.EndsWith("a.par2", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x).ToList();
        foreach (var f in aVolumes.Take(2))
            File.Move(f, f + ".hidden");

        // Set B: damage 2 blocks, all recovery present.
        File.WriteAllBytes(bFile, WithDamagedBlocks(bBackup, 64, 4, 11));

        var log = new List<string>();
        var mon = new Par2Monitor(_dir, new MonitorOptions { Log = log.Add });
        await mon.RunOnceAsync();

        // B was repaired and its .par2 files removed; A was left for later.
        Assert.Equal(bBackup, File.ReadAllBytes(bFile));
        Assert.NotEqual(aBackup, File.ReadAllBytes(aFile));
        Assert.Contains(log, l => l.Contains("not enough recovery blocks"));
        Assert.True(File.Exists(aFile));
    }

    [Fact]
    public async Task DoesNotRepeatedlyProcessUnchangedCompleteSet()
    {
        MakeFile(_dir, "x.bin", 800, 3);
        var opt = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 2, RecoveryFileCount = 1, Quiet = true, Log = _ => { } };
        await Par2Writer.CreateAsync(opt, Path.Combine(_dir, "set.par2"), new[] { Path.Combine(_dir, "x.bin") });

        int repairReports = 0;
        Action<string> log = s => { if (s.Contains("verified OK") || s.Contains("repaired")) repairReports++; };
        var mon = new Par2Monitor(_dir, new MonitorOptions { Log = log });

        await mon.RunOnceAsync();
        await mon.RunOnceAsync();
        await mon.RunOnceAsync();

        // The complete, intact set should be processed at most once.
        Assert.True(repairReports <= 1, $"expected at most 1 ok report, got {repairReports}");
    }

    [Fact]
    public async Task UnrelatedFileChangesDoNotTriggerReprocess()
    {
        // Simulates a log file living in the monitored directory and being
        // appended to between scans; the set must not be reprocessed for it.
        MakeFile(_dir, "d.bin", 1000, 11);
        var opt = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 2, RecoveryFileCount = 1, Quiet = true, Log = _ => { } };
        await Par2Writer.CreateAsync(opt, Path.Combine(_dir, "set.par2"), new[] { Path.Combine(_dir, "d.bin") });

        var unrelated = Path.Combine(_dir, "app.log");
        int repairReports = 0;
        Action<string> log = s => { if (s.Contains("verified OK") || s.Contains("repaired")) repairReports++; };
        var mon = new Par2Monitor(_dir, new MonitorOptions { Log = log });

        await mon.RunOnceAsync(); // first process
        int afterFirst = repairReports;

        // Append to an unrelated file (like the tool's own log) and rescan.
        File.AppendAllText(unrelated, "more log\n");
        await mon.RunOnceAsync();

        Assert.Equal(afterFirst, repairReports); // no extra processing
        Assert.True(afterFirst >= 1, "expected the set to be processed at least once");
    }
}
