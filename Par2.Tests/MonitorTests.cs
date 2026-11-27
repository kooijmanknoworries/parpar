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

    [Fact]
    public async Task RepairsSetOnceEnoughRecoveryArrives()
    {
        // Create data + a full recovery set, then hide some recovery volumes so
        // the set is initially incomplete, and reveal them so repair succeeds.
        string dataName = "data.bin";
        MakeFile(_dir, dataName, 2000, 1);
        var dataFile = Path.Combine(_dir, dataName);
        var dataBackup = File.ReadAllBytes(dataFile);

        // Create a set with 8 recovery blocks in 8 files so each volume holds 1.
        var opt = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 8, RecoveryFileCount = 8, Quiet = true, Log = _ => { } };
        var result = await Par2Writer.CreateAsync(opt, Path.Combine(_dir, "set.par2"), new[] { dataFile });

        // Hide every recovery volume except the first, so only 1/8 blocks present.
        var allPar2 = Directory.GetFiles(_dir, "*.par2").OrderBy(x => x).ToList();
        var hide = allPar2.Where(f => f != Path.Combine(_dir, "set.par2")).ToList();
        foreach (var f in hide)
            File.Move(f, f + ".hidden");

        // Damage the data file.
        var damaged = (byte[])dataBackup.Clone();
        damaged[100] = 0xFF; damaged[101] = 0x55;
        File.WriteAllBytes(dataFile, damaged);

        var log = new List<string>();
        var mon = new Par2Monitor(_dir, new MonitorOptions { ThresholdPercent = 100, Log = log.Add });
        await mon.RunOnceAsync();

        // Not enough recovery yet -> should not have repaired.
        var afterFirst = File.ReadAllBytes(dataFile);
        Assert.NotEqual(dataBackup, afterFirst); // still damaged
        Assert.DoesNotContain(log, l => l.Contains("repaired"));

        // Reveal all recovery volumes.
        foreach (var f in hide)
            File.Move(f + ".hidden", f);

        await mon.RunOnceAsync();

        Assert.Equal(dataBackup, File.ReadAllBytes(dataFile));
        Assert.Contains(log, l => l.Contains("repaired"));
    }

    [Fact]
    public async Task DoesNotRepairUntilThresholdReached()
    {
        string dataName = "data.bin";
        MakeFile(_dir, dataName, 1200, 7);
        var dataFile = Path.Combine(_dir, dataName);
        var backup = File.ReadAllBytes(dataFile);

        var opt = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 4, RecoveryFileCount = 4, Quiet = true, Log = _ => { } };
        await Par2Writer.CreateAsync(opt, Path.Combine(_dir, "set.par2"), new[] { dataFile });

        var allPar2 = Directory.GetFiles(_dir, "*.par2").OrderBy(x => x).ToList();
        var hide = allPar2.Where(f => f != Path.Combine(_dir, "set.par2")).ToList();
        // Keep only 2 of the 4 recovery volumes.
        foreach (var f in hide.Take(2))
            File.Move(f, f + ".hidden");

        var damaged = (byte[])backup.Clone();
        damaged[50] ^= 0xAA;
        File.WriteAllBytes(dataFile, damaged);

        var log = new List<string>();
        var mon = new Par2Monitor(_dir, new MonitorOptions { ThresholdPercent = 100, Log = log.Add });
        await mon.RunOnceAsync();

        // Only 2/4 present -> no repair yet.
        var now = File.ReadAllBytes(dataFile);
        Assert.NotEqual(backup, now);
        Assert.DoesNotContain(log, l => l.Contains("repaired"));

        // Reveal the rest.
        foreach (var f in hide.Take(2))
            File.Move(f + ".hidden", f);
        await mon.RunOnceAsync();

        Assert.Equal(backup, File.ReadAllBytes(dataFile));
    }

    [Fact]
    public async Task DoesNotRepeatedlyProcessUnchangedCompleteSet()
    {
        MakeFile(_dir, "x.bin", 800, 3);
        var opt = new CreateOptions { BlockSize = 64, RecoveryBlockCount = 2, RecoveryFileCount = 1, Quiet = true, Log = _ => { } };
        await Par2Writer.CreateAsync(opt, Path.Combine(_dir, "set.par2"), new[] { Path.Combine(_dir, "x.bin") });

        int repairReports = 0;
        Action<string> log = s => { if (s.Contains("verified OK") || s.Contains("repaired")) repairReports++; };
        var mon = new Par2Monitor(_dir, new MonitorOptions { ThresholdPercent = 100, Log = log });

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
        var mon = new Par2Monitor(_dir, new MonitorOptions { ThresholdPercent = 100, Log = log });

        await mon.RunOnceAsync(); // first process
        int afterFirst = repairReports;

        // Append to an unrelated file (like the tool's own log) and rescan.
        File.AppendAllText(unrelated, "more log\n");
        await mon.RunOnceAsync();

        Assert.Equal(afterFirst, repairReports); // no extra processing
        Assert.True(afterFirst >= 1, "expected the set to be processed at least once");
    }
}
