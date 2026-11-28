using System.Text;

namespace Par2.Core;

/// <summary>
/// One repair in progress, for UI status bars: which set, which file of the
/// set is being written, and the percentage of the set completed.
/// </summary>
public sealed record RepairProgress(string SetId, int Percent, string? CurrentFile);

public sealed class MonitorOptions
{
    /// <summary>Seconds between directory scans. Default 5.</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>
    /// After a set's files are verified intact or fully repaired, delete the
    /// set's .par2 files so the download folder does not fill up. Default
    /// true.
    /// </summary>
    public bool RemoveParFilesAfterRepair { get; set; } = true;

    public bool Quiet { get; set; }

    public Action<string>? Log { get; set; } = Console.WriteLine;

    /// <summary>
    /// Called with <see cref="null"/> when no set is being repaired and with
    /// progress updates (percentage 0-100) while a set is repaired.
    /// </summary>
    public Action<RepairProgress?>? RepairProgress { get; set; }

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
/// Sets are processed one at a time, in order. A repair is attempted for each
/// set that is ready to be checked; if the set's files need repair but there
/// are not enough recovery blocks, the monitor says so and simply moves on to
/// the next set — it never blocks on one incomplete set. A set that still
/// needs repair is revisited on later scans as recovery files arrive. When a
/// set is verified or repaired, its .par2 files are removed (unless
/// <see cref="MonitorOptions.RemoveParFilesAfterRepair"/> is off).
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
                    $"repairing sets as soon as enough recovery blocks are present (sets that are still incomplete are revisited on later scans).");
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
        _opt.RepairProgress?.Invoke(null);
    }

    /// <summary>Run a single scan/repair cycle (returns when the scan is done).</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        var scan = Par2Scanner.ScanDirectory(_directory);

        AnnounceScanSummary(scan);
        foreach (var bad in scan.Unreadable)
        {
            var key = Path.GetFileName(bad.Path);
            var st = GetFileState(key);
            if (st.Announced != bad.Reason)
            {
                st.Announced = bad.Reason;
                _opt.Report($"'{key}' is not a usable PAR 2 file: {bad.Reason}");
            }
        }

        foreach (var set in scan.Sets)
        {
            ct.ThrowIfCancellationRequested();
            await ProcessSetAsync(set, ct);
        }
    }

    private readonly Dictionary<string, SetState> _fileState = new(StringComparer.OrdinalIgnoreCase);

    private SetState GetFileState(string name)
    {
        if (!_fileState.TryGetValue(name, out var state))
        {
            state = new SetState();
            _fileState[name] = state;
        }
        return state;
    }

    // One line per scan, logged only when the summary changes, so the user
    // always sees what the monitor is looking at and why it is (or is not)
    // waiting.
    private string? _lastSummary;

    private void AnnounceScanSummary(Par2Scanner.ScanResult scan)
    {
        int parFiles = scan.Sets.SelectMany(s => s.Files).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                         + scan.Unreadable.Count;

        int ready = 0, noMain = 0, noRecovery = 0;
        foreach (var set in scan.Sets)
        {
            if (!set.HasMain)
                noMain++;
            else if (set.Recovery.Count == 0)
                noRecovery++;
            else
                ready++;
        }

        string summary = $"Scan: {parFiles} .par2 file(s) found; " +
                         $"{ready} set(s) with main packet and recovery data, " +
                         $"{noRecovery} set(s) without recovery data yet, " +
                         $"{noMain} set(s) without a main packet yet.";
        if (_lastSummary != summary)
        {
            _lastSummary = summary;
            _opt.Report(summary);
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
        {
            if (set.Recovery.Count > 0)
                Announce(GetState(set.SetId), $"nomain/{set.Recovery.Count}",
                    $"set {ShortId(set.SetId)}: has {set.Recovery.Count} recovery block(s) but no main packet yet; waiting for the set's main .par2 file");
            return;
        }

        if (set.Recovery.Count == 0)
        {
            Announce(GetState(set.SetId), "none", $"set {ShortId(set.SetId)}: no recovery data yet");
            return;
        }

        var state = GetState(set.SetId);

        // Check the set only when something changed since the last attempt
        // (recovery blocks arrived, a source file was modified, ...). A set
        // that still needs repair is simply revisited on the next scan where
        // its state changed; the monitor never waits on it.
        string fingerprint = ComputeFingerprint(set);
        if (state.Attempted && state.Fingerprint == fingerprint)
            return;

        state.Attempted = true;
        state.Fingerprint = fingerprint;

        // Only one set is repaired at a time, in order. The progress callback
        // identifies the set and how far its repair has come, so the UI can
        // show a progress bar that fills to 100% per set.
        var progress = new Progress<int>(p =>
            _opt.RepairProgress?.Invoke(new RepairProgress(set.SetId, p, null)));

        var repairer = new Par2Repairer(_opt.Log, _opt.Quiet);
        RepairReport report;
        try
        {
            _opt.RepairProgress?.Invoke(new RepairProgress(set.SetId, 0, null));
            report = await repairer.RunAsync(set, _directory, repair: true, ct, progress);
        }
        catch (Exception e)
        {
            _opt.Report($"set {ShortId(set.SetId)}: repair error: {e.Message}");
            return;
        }
        finally
        {
            _opt.RepairProgress?.Invoke(null);
        }

        switch (report.Status)
        {
            case RepairStatus.Ok:
                _opt.Report($"set {ShortId(set.SetId)}: all {report.SourceFileCount} file(s) verified OK");
                MaybeRemoveParFiles(set, state, verified: true);
                break;
            case RepairStatus.Repaired:
                foreach (var f in report.Files)
                    if (f.Problem != "ok")
                        _opt.Report($"set {ShortId(set.SetId)}: repaired '{f.Name}' ({f.Problem})");
                _opt.Report($"set {ShortId(set.SetId)}: {report.Message}");
                MaybeRemoveParFiles(set, state, verified: false);
                break;
            case RepairStatus.NotEnoughRecovery:
                _opt.Report($"set {ShortId(set.SetId)}: not enough recovery blocks yet ({report.RecoveryBlocksAvailable} available, {report.MissingBlocks} missing); moving on and waiting for more .par2 files");
                break;
            case RepairStatus.NoFiles:
                _opt.Report($"set {ShortId(set.SetId)}: {report.Message}");
                break;
            default:
                _opt.Report($"set {ShortId(set.SetId)}: {report.Message}");
                break;
        }
    }

    /// <summary>Remove the set's .par2 files after a successful verify/repair.</summary>
    private void MaybeRemoveParFiles(RecoverySet set, SetState state, bool verified)
    {
        if (!_opt.RemoveParFilesAfterRepair)
            return;
        state.Announced = null; // re-announce if the set reappears
        int removed = 0;
        foreach (var file in set.Files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch
            {
                // the file is locked or gone; leave it for a later scan
            }
        }
        if (removed > 0)
            _opt.Report($"set {ShortId(set.SetId)}: {(verified ? "verified, " : "repaired, ")}removed {removed} .par2 file(s)");
        else
            _opt.Report($"set {ShortId(set.SetId)}: .par2 files already gone");
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

                // The presence of the set's own .par2 files matters: a set
                // whose volumes were removed (after a successful repair) or
                // re-added by the downloader must be re-examined.
                foreach (var par2 in set.Files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    sb.Append(Path.GetFileName(par2)).Append(File.Exists(par2) ? 1 : 0).Append(',');
            }
        }
        catch
        {
            // ignore transient directory errors
        }
        return sb.ToString();
    }
}
