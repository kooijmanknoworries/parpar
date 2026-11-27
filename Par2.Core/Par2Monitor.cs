using System.Text;

namespace Par2.Core;

public sealed class MonitorOptions
{
    /// <summary>Seconds between directory scans. Default 5.</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Minimum percentage of the set's recovery blocks that must be present
    /// before repair is attempted. The expected count is the highest recovery
    /// exponent seen in the set plus one, which grows as more recovery files
    /// arrive. Default 100 (repair only when there are no gaps below the
    /// highest exponent seen).
    /// </summary>
    public int ThresholdPercent { get; set; } = 100;

    public bool Quiet { get; set; }

    public Action<string>? Log { get; set; } = Console.WriteLine;

    public void Report(string message, bool onlyWhenNotQuiet = false)
    {
        if (!Quiet || !onlyWhenNotQuiet)
            Log?.Invoke(message);
    }
}

/// <summary>
/// Watches a directory for PAR 2.0 recovery sets and automatically starts a
/// repair as soon as a set has enough recovery data.
///
/// On every scan the directory is grouped into recovery sets (by set id).
/// For each set with a main packet the monitor compares the number of
/// recovery blocks present with the highest recovery exponent seen: once at
/// least <see cref="MonitorOptions.ThresholdPercent"/> percent of those
/// blocks are available, a repair of the set's source files (which are looked
/// up in the same directory) is attempted.
///
/// A set is only re-processed when something changes (more recovery data
/// arrives, or a data file appears, changes, or is re-damaged), so an intact
/// or permanently unrecoverable set does not cause repeated work.
/// </summary>
public sealed class Par2Monitor
{
    private sealed class SetState
    {
        public bool Attempted;
        public string? Fingerprint;
        public string? Announced;
    }

    private readonly string _directory;
    private readonly MonitorOptions _opt;
    private readonly Dictionary<string, SetState> _state = new(StringComparer.Ordinal);

    public Par2Monitor(string directory, MonitorOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory is required.", nameof(directory));
        _directory = Path.GetFullPath(directory);
        _opt = options ?? new MonitorOptions();
    }

    /// <summary>Continuously monitor the directory until cancelled.</summary>
    public async Task MonitorAsync(CancellationToken ct = default)
    {
        _opt.Report($"Watching '{_directory}' every {Math.Max(1, _opt.IntervalSeconds)}s; " +
                    $"repairing sets once >= {_opt.ThresholdPercent}% of recovery blocks are present.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _opt.Report($"Monitor error: {e.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opt.IntervalSeconds)), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>Run a single scan/repair cycle (returns when the scan is done).</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        List<RecoverySet> sets;
        try
        {
            sets = Par2Scanner.ScanDirectory(_directory);
        }
        catch (Exception e)
        {
            _opt.Report($"Could not scan '{_directory}': {e.Message}");
            return;
        }

        foreach (var set in sets)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessSetAsync(set, ct);
        }
    }

    private string ShortId(string setId) => setId.Length >= 8 ? setId[..8] : setId;

    private SetState GetState(string setId)
    {
        if (!_state.TryGetValue(setId, out var state))
        {
            state = new SetState();
            _state[setId] = state;
        }
        return state;
    }

    private void Announce(SetState state, string key, string message)
    {
        if (state.Announced == key)
            return;
        state.Announced = key;
        _opt.Report(message);
    }

    private async Task ProcessSetAsync(RecoverySet set, CancellationToken ct)
    {
        if (!set.HasMain)
            return; // no main packet: not a usable set yet

        int available = set.Recovery.Count;
        int expected = set.RecoveryBlockCount;

        if (available == 0 || expected <= 0)
        {
            Announce(GetState(set.SetId), "none", $"set {ShortId(set.SetId)}: no recovery data yet");
            return;
        }

        int required = (int)Math.Ceiling(expected * _opt.ThresholdPercent / 100.0);
        if (required < 1)
            required = 1;

        var state = GetState(set.SetId);

        if (available < required)
        {
            Announce(state, $"{available}/{expected}",
                $"set {ShortId(set.SetId)}: recovery data incomplete ({available}/{expected} blocks; " +
                $"{required} needed for the {_opt.ThresholdPercent}% threshold)");
            return;
        }

        // Enough recovery data: attempt repair only when something changed
        // since the last attempt (or this is the first attempt).
        string fingerprint = ComputeFingerprint(set);
        if (state.Attempted && state.Fingerprint == fingerprint)
            return;

        state.Attempted = true;
        state.Fingerprint = fingerprint;

        var repairer = new Par2Repairer(_opt.Log, _opt.Quiet);
        RepairReport report;
        try
        {
            report = await repairer.RunAsync(set, _directory, repair: true, ct);
        }
        catch (Exception e)
        {
            _opt.Report($"set {ShortId(set.SetId)}: repair error: {e.Message}");
            return;
        }

        switch (report.Status)
        {
            case RepairStatus.Ok:
                _opt.Report($"set {ShortId(set.SetId)}: all {report.SourceFileCount} file(s) verified OK");
                break;
            case RepairStatus.Repaired:
                foreach (var f in report.Files)
                    if (f.Problem != "ok")
                        _opt.Report($"set {ShortId(set.SetId)}: repaired '{f.Name}' ({f.Problem})");
                _opt.Report($"set {ShortId(set.SetId)}: {report.Message}");
                break;
            case RepairStatus.NoFiles:
                _opt.Report($"set {ShortId(set.SetId)}: waiting for data files to appear in '{_directory}'");
                break;
            case RepairStatus.NotEnoughRecovery:
                _opt.Report($"set {ShortId(set.SetId)}: {report.Message}");
                break;
            default:
                _opt.Report($"set {ShortId(set.SetId)}: {report.Message}");
                break;
        }
    }

    // A fingerprint of the things that could affect the outcome of a repair:
    // the available/expected recovery counts and, for each file in the set,
    // the size and last write time of the file that would be matched (by name,
    // or by size). Unrelated files (such as this tool's own log) are
    // deliberately excluded so that ordinary logging does not cause the set to
    // be re-processed on every scan.
    private string ComputeFingerprint(RecoverySet set)
    {
        var sb = new StringBuilder();
        sb.Append(set.Recovery.Count).Append(':').Append(set.RecoveryBlockCount).Append(':');
        try
        {
            if (Directory.Exists(_directory))
            {
                var byName = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                var bySize = new Dictionary<long, List<FileInfo>>();
                foreach (string f in Directory.EnumerateFiles(_directory))
                {
                    if (f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var info = new FileInfo(f);
                    if (!info.Exists)
                        continue;
                    byName[info.Name] = info;
                    if (!bySize.TryGetValue(info.Length, out var list))
                        bySize[info.Length] = list = new List<FileInfo>();
                    list.Add(info);
                }

                foreach (var desc in set.Descriptions.OrderBy(d => d.FileName, StringComparer.Ordinal))
                {
                    sb.Append(desc.FileName).Append('#');
                    if (byName.TryGetValue(desc.FileName, out var named))
                    {
                        sb.Append(named.Length).Append(',').Append(named.LastWriteTimeUtc.Ticks).Append(';');
                    }
                    else if (bySize.TryGetValue((long)desc.FileLength, out var sized))
                    {
                        sb.Append('{');
                        foreach (var fi in sized.OrderBy(x => x.Name, StringComparer.Ordinal))
                            sb.Append(fi.Name).Append(fi.Length).Append(fi.LastWriteTimeUtc.Ticks).Append(',');
                        sb.Append("};");
                    }
                    else
                    {
                        sb.Append('-');
                    }
                }
            }
        }
        catch
        {
            // ignore transient directory errors
        }
        return sb.ToString();
    }
}
