using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Par2.Core;

namespace Par2.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<string> _explicitFiles = new();
    private CancellationTokenSource? _cts;
    private Par2Monitor? _monitor;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        Loaded += OnLoaded;
    }

    // ---- lifecycle ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        UpdateFileList();
        if (App.AutoStartMonitor && MonDirBox.Text.Length > 0 && Directory.Exists(MonDirBox.Text.Trim()))
            StartMonitorFromUi();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopMonitorCore();
        _cts?.Cancel();
    }

    // ---- settings <-> UI ----

    private void LoadSettingsIntoUi()
    {
        var s = _settings;

        // Create
        DirBox.Text = s.ProtectDirectory;
        AllFilesCheck.IsChecked = s.ProtectAllFiles;
        FillRedundancyCombo();
        RedundancyCombo.SelectedItem = s.RedundancyPercent.ToString();

        BlockSizeModeCombo.Items.Add("Automatic (recommended)");
        BlockSizeModeCombo.Items.Add("Manual (KB)");
        BlockSizeModeCombo.SelectedIndex = string.Equals(s.BlockSizeMode, "manual", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        ManualBlockSizeBox.Text = s.ManualBlockSizeKb.ToString();
        ManualBlockSizeBox.IsEnabled = BlockSizeModeCombo.SelectedIndex == 1;
        BlockSizeModeCombo.SelectionChanged += (_, _) =>
            ManualBlockSizeBox.IsEnabled = BlockSizeModeCombo.SelectedIndex == 1;

        RecFileCombo.Items.Add("Default");
        RecFileCombo.Items.Add("1");
        RecFileCombo.Items.Add("2");
        RecFileCombo.Items.Add("4");
        RecFileCombo.Items.Add("8");
        // combo index: 0=Default, 1=1, 2=2, 3=4, 4=8
        RecFileCombo.SelectedIndex = RecFileCountFromSettings(s.RecoveryFileCount);

        SchemeCombo.Items.Add("Automatic (variable)");
        SchemeCombo.Items.Add("Uniform");
        SchemeCombo.Items.Add("Limited");
        SchemeCombo.SelectedIndex = s.RecoveryFileScheme switch
        {
            "uniform" => 1,
            "limited" => 2,
            _ => 0
        };

        FirstBlockBox.Text = s.FirstRecoveryBlock.ToString();
        ParNameBox.Text = "";

        // Monitor
        MonDirBox.Text = s.MonitorDirectory;
        MonIntervalBox.Text = s.MonitorIntervalSeconds.ToString();
        MonRemoveParCheck.IsChecked = s.MonitorRemoveParFilesAfterRepair;
    }

    private int RecFileCountFromSettings(int rfc)
    {
        return rfc switch { 0 => 0, 1 => 1, 2 => 2, 4 => 3, 8 => 4, _ => 0 };
    }

    private int SelectedRecoveryFileCount()
    {
        return RecFileCombo.SelectedIndex switch
        {
            1 => 1, 2 => 2, 3 => 4, 4 => 8, _ => 0
        };
    }

    private void SaveSettingsFromUi()
    {
        var s = _settings;
        s.ProtectDirectory = DirBox.Text.Trim();
        s.ProtectAllFiles = AllFilesCheck.IsChecked == true;
        if (RedundancyCombo.SelectedItem is string r && int.TryParse(r, out int rp))
            s.RedundancyPercent = rp;
        s.BlockSizeMode = BlockSizeModeCombo.SelectedIndex == 1 ? "manual" : "auto";
        if (int.TryParse(ManualBlockSizeBox.Text.Trim(), out int kb) && kb > 0)
            s.ManualBlockSizeKb = kb;
        s.RecoveryFileCount = SelectedRecoveryFileCount();
        s.RecoveryFileScheme = SchemeCombo.SelectedIndex switch { 1 => "uniform", 2 => "limited", _ => "auto" };
        if (int.TryParse(FirstBlockBox.Text.Trim(), out int fb))
            s.FirstRecoveryBlock = Math.Max(0, fb);

        s.MonitorDirectory = MonDirBox.Text.Trim();
        if (int.TryParse(MonIntervalBox.Text.Trim(), out int iv) && iv > 0)
            s.MonitorIntervalSeconds = iv;
        s.MonitorRemoveParFilesAfterRepair = MonRemoveParCheck.IsChecked == true;

        s.Save();
    }

    private void FillRedundancyCombo()
    {
        foreach (int v in new[] { 5, 10, 15, 20, 25, 30, 40, 50 })
            RedundancyCombo.Items.Add(v.ToString());
    }

    // ---- file list (create) ----

    private void UpdateFileList()
    {
        FileList.Items.Clear();
        string dir = DirBox.Text.Trim();
        bool all = AllFilesCheck.IsChecked == true;

        if (!all)
        {
            foreach (var f in _explicitFiles)
                FileList.Items.Add(f);
            UpdateCreateStatus();
            return;
        }

        if (dir.Length > 0 && Directory.Exists(dir))
        {
            foreach (var f in SafeEnumerate(dir))
            {
                FileList.Items.Add(f);
            }
        }
        UpdateCreateStatus();
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir)
                   .Where(f => !f.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) &&
                              !f.EndsWith(".check.par2", StringComparison.OrdinalIgnoreCase))
                   .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return Enumerable.Empty<string>();
        }
    }

    private List<string> SelectedFiles()
    {
        if (AllFilesCheck.IsChecked == true)
        {
            string dir = DirBox.Text.Trim();
            return dir.Length > 0 ? SafeEnumerate(dir).ToList() : new List<string>();
        }
        return _explicitFiles.Distinct().ToList();
    }

    private void UpdateCreateStatus()
    {
        int n = FileList.Items.Count;
        string dir = DirBox.Text.Trim();
        string outName = ParNameBox.Text.Trim();
        string parStem = outName.Length == 0
            ? (dir.Length > 0 ? Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)) + ".par2" : "<folder>.par2")
            : (outName.EndsWith(".par2", StringComparison.OrdinalIgnoreCase) ? outName : outName + ".par2");
        OutDirText.Text = n == 0
            ? "Select a directory or add files to protect."
            : $"{n} file(s) will be protected. Output: {parStem} (+ volumes) next to the files.";
    }

    // ---- create ----

    private async void CreateClick(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        var files = SelectedFiles();
        if (files.Count == 0)
        {
            MessageBox.Show(this, "No source files selected.", "ParPar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string dir = DirBox.Text.Trim();
        string outName = ParNameBox.Text.Trim();
        string baseName = outName.Length == 0
            ? (dir.Length > 0 ? Path.Combine(dir, Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar))) : "recovery")
            : outName;

        var opt = new CreateOptions
        {
            Log = msg => Dispatcher.Invoke(() => AppendStatus(msg)),
            Quiet = false,
            RedundancyPercent = _settings.RedundancyPercent,
            FirstRecoveryBlock = _settings.FirstRecoveryBlock,
            RecoveryFileCount = _settings.RecoveryFileCount,
            Scheme = _settings.RecoveryFileScheme switch
            {
                "uniform" => RecoveryFileScheme.Uniform,
                "limited" => RecoveryFileScheme.Limited,
                _ => RecoveryFileScheme.Variable
            }
        };

        if (BlockSizeModeCombo.SelectedIndex == 1 &&
            int.TryParse(ManualBlockSizeBox.Text.Trim(), out int kb) && kb > 0)
            opt.BlockSize = (long)kb * 1024;

        _cts = new CancellationTokenSource();
        SetRunning(true);
        try
        {
            var result = await Par2Writer.CreateAsync(opt, baseName, files.ToArray(), _cts.Token);
            string outDir = Path.GetDirectoryName(Path.GetFullPath(baseName)) ?? ".";
            MessageBox.Show(this,
                $"Created {result.RecoveryBlockCount} recovery block(s) for {result.SourceFileCount} file(s).\n\n" +
                $"Set ID: {result.SetId}\nOutput in: {outDir}",
                "ParPar", MessageBoxButton.OK, MessageBoxImage.Information);
            AppendStatus("Done. Recovery volumes written.");
        }
        catch (OperationCanceledException)
        {
            AppendStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            AppendStatus("Error: " + ex.Message);
            MessageBox.Show(this, "Create failed:\n" + ex.Message, "ParPar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunning(false);
        }
    }

    // ---- verify & repair ----

    private async void VerifyClick(object sender, RoutedEventArgs e) => await RunVerifyAsync(repair: false);
    private async void RepairClick(object sender, RoutedEventArgs e) => await RunVerifyAsync(repair: true);

    private async Task RunVerifyAsync(bool repair)
    {
        SaveSettingsFromUi();
        string par = ParBox.Text.Trim();
        if (par.Length == 0 || !File.Exists(par))
        {
            MessageBox.Show(this, "Choose an existing .par2 file first.", "ParPar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(par));
        VerifyDirText.Text = "Directory scanned: " + dir;

        var scan = Par2Scanner.ScanDirectory(dir!);
        string? setId = SetIdOf(par);
        var set = scan.Sets.FirstOrDefault(s => setId != null && s.SetId == setId) ?? scan.Sets.FirstOrDefault();
        if (set == null || !set.HasMain)
        {
            string detail = "";
            var own = scan.Unreadable.FirstOrDefault(u => Path.GetFileName(u.Path) == Path.GetFileName(par));
            if (own != null)
                detail = "\n\n" + Path.GetFileName(par) + ": " + own.Reason;
            AppendStatus("No valid PAR 2.0 recovery set found for this file.");
            MessageBox.Show(this, "No valid PAR 2.0 recovery set found for this file.\nMake sure all volumes are in the same folder." + detail,
                "ParPar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        VerifyList.Items.Clear();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        VerifyProgress.Value = 0;
        try
        {
            var verifyProgress = new Progress<int>(p => Dispatcher.Invoke(() => VerifyProgress.Value = p));
            var repairer = new Par2Repairer(msg => Dispatcher.Invoke(() => AppendStatus(msg)), false);
            var report = await repairer.RunAsync(set, dir!, repair, _cts.Token, verifyProgress);
            VerifyProgress.Value = 100;
            foreach (var f in report.Files)
                VerifyList.Items.Add($"{f.Name} — {f.Problem}");
            AppendStatus(report.Message);

            if (report.Status is RepairStatus.Ok)
                MessageBox.Show(this, "All source files verified OK.", "ParPar", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (report.Status == RepairStatus.Repaired)
                MessageBox.Show(this, report.Message, "ParPar", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            AppendStatus("Error: " + ex.Message);
            MessageBox.Show(this, ex.Message, "ParPar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunning(false);
        }
    }

    private static string? SetIdOf(string parName)
    {
        try
        {
            var data = File.ReadAllBytes(parName);
            int offset = 0;
            while (offset + Par2Format.HeaderSize <= data.Length)
            {
                var packet = Par2Packet.TryParse(data, offset, data.Length - offset);
                if (packet == null) break;
                if (packet.Kind == Par2PacketKind.Main)
                    return packet.SetId.ToString();
                offset += (int)packet.Length;
            }
        }
        catch { }
        return null;
    }

    // ---- monitor ----

    private void MonStartClick(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        StartMonitorFromUi();
    }

    private void StartMonitorFromUi()
    {
        string dir = MonDirBox.Text.Trim();
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            MessageBox.Show(this, "Choose an existing directory to watch.", "ParPar", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int interval = int.TryParse(MonIntervalBox.Text.Trim(), out int iv) ? Math.Max(1, iv) : 5;

        var opts = new MonitorOptions
        {
            IntervalSeconds = interval,
            Quiet = false,
            RemoveParFilesAfterRepair = MonRemoveParCheck.IsChecked == true,
            Log = msg => Dispatcher.Invoke(() => AddMonitorLog(msg)),
            RepairProgress = prog => Dispatcher.Invoke(() => UpdateMonitorProgress(prog))
        };

        _cts = new CancellationTokenSource();
        _monitor = new Par2Monitor(dir, opts);
        _ = Task.Run(async () =>
        {
            try { await _monitor.MonitorAsync(_cts.Token); }
            catch (OperationCanceledException) { }
        });

        MonStartBtn.IsEnabled = false;
        MonStopBtn.IsEnabled = true;
        CancelBtn.IsEnabled = true;
        AddMonitorLog($"Started monitoring '{dir}' every {interval}s.");
    }

    /// <summary>Append a line to the activity log and keep the newest line visible.</summary>
    private void AddMonitorLog(string message)
    {
        MonList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        MonList.ScrollIntoView(MonList.Items[MonList.Items.Count - 1]);
        MonList.SelectedItem = MonList.Items[MonList.Items.Count - 1];
    }

    private void MonStopClick(object sender, RoutedEventArgs e)
    {
        StopMonitorCore();
        AddMonitorLog("Monitor stopped.");
    }

    private void StopMonitorCore()
    {
        _cts?.Cancel();
        _monitor = null;
        MonStartBtn.IsEnabled = true;
        MonStopBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        MonProgress.Value = 0;
        MonProgressText.Text = "Idle — no set is being repaired.";
    }

    private void UpdateMonitorProgress(RepairProgress? prog)
    {
        if (prog == null)
        {
            MonProgress.Value = 0;
            MonProgressText.Text = "Idle — no set is being repaired.";
            return;
        }
        MonProgress.Value = prog.Percent;
        string setId = prog.SetId.Length >= 8 ? prog.SetId[..8] : prog.SetId;
        MonProgressText.Text = prog.Percent >= 100
            ? $"Set {setId}: done."
            : $"Repairing set {setId}: {prog.Percent}%";
    }

    // ---- dialogs ----

    private void BrowseDirClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder to protect" };
        if (Directory.Exists(DirBox.Text.Trim()))
            dlg.InitialDirectory = DirBox.Text.Trim();
        if (dlg.ShowDialog(this) == true)
        {
            DirBox.Text = dlg.FolderName;
            UpdateFileList();
        }
    }

    private void BrowseParClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose a PAR 2.0 file", Filter = "PAR 2.0 files (*.par2)|*.par2|All files (*)|*.*" };
        if (dlg.ShowDialog(this) == true)
            ParBox.Text = dlg.FileName;
    }

    private void BrowseMonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder to monitor" };
        if (Directory.Exists(MonDirBox.Text.Trim()))
            dlg.InitialDirectory = MonDirBox.Text.Trim();
        if (dlg.ShowDialog(this) == true)
            MonDirBox.Text = dlg.FolderName;
    }

    private void AddFilesClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Add files", Multiselect = true, Filter = "All files (*)|*.*" };
        if (dlg.ShowDialog(this) == true)
        {
            foreach (var f in dlg.FileNames)
                if (!_explicitFiles.Contains(f))
                    _explicitFiles.Add(f);
            AllFilesCheck.IsChecked = false;
            UpdateFileList();
        }
    }

    private void RemoveFilesClick(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is string sel && _explicitFiles.Remove(sel))
            UpdateFileList();
    }

    private void ClearFilesClick(object sender, RoutedEventArgs e)
    {
        _explicitFiles.Clear();
        UpdateFileList();
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StopMonitorCore();
    }

    // ---- helpers ----

    private void AppendStatus(string text)
    {
        StatusText.Text = text;
    }

    private void SetRunning(bool running)
    {
        CreateBtn.IsEnabled = !running;
        CancelBtn.IsEnabled = running;
        VerifyList.IsEnabled = !running;
    }
}
