using Par2.Core;

namespace Par2.Cli;

/// <summary>
/// Command line interface in the style of par2cmdline / QuickPar.
///
///   par2 create [options] &lt;PAR2 file&gt; [files]   : Create PAR2 files
///   par2 verify [options] &lt;PAR2 file&gt; [files]   : Verify files using PAR2 files
///   par2 repair [options] &lt;PAR2 file&gt; [files]   : Repair files using PAR2 files
/// </summary>
internal static class Program
{
    private static int _exitCode = 0;

    private static int Main(string[] args)
    {
        try
        {
            _exitCode = Run(args);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 1;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        return _exitCode;
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string op = args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();

        return op switch
        {
            "create" or "c" => DoCreate(rest),
            "verify" or "v" => DoVerify(rest, repair: false),
            "repair" or "r" => DoVerify(rest, repair: true),
            "monitor" or "m" => DoMonitor(rest),
            "-v" or "--version" or "version" => PrintVersion(),
            "-h" or "--help" or "help" or "?" => 0,
            _ => 1
        };
    }

    private static int DoMonitor(string[] args)
    {
        var opt = new MonitorOptions();
        string? directory = null;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("-"))
            {
                switch (a[1])
                {
                    case 't':
                    {
                        long n = ParseNumber(a, 2, out bool ok);
                        if (!ok) { Error("Invalid interval option; expected -t<seconds>."); return 1; }
                        opt.IntervalSeconds = (int)n;
                        break;
                    }
                    case 'k':
                        opt.RemoveParFilesAfterRepair = false;
                        break;
                    case 'q':
                        opt.Quiet = true;
                        break;
                    default:
                        Error($"Unknown option: {a}");
                        return 1;
                }
            }
            else
            {
                directory = a;
            }
        }

        if (directory == null)
        {
            Error("A directory to monitor is required.");
            PrintUsage();
            return 1;
        }
        if (!Directory.Exists(directory))
        {
            Error($"Directory does not exist: {directory}");
            return 1;
        }

        var monitor = new Par2Monitor(directory, opt);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        monitor.MonitorAsync(cts.Token).GetAwaiter().GetResult();
        opt.Log?.Invoke("Stopped monitoring.");
        return 0;
    }

    private static int DoCreate(string[] args)
    {
        var opt = new CreateOptions { Log = Console.WriteLine };
        string? parName = null;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("-"))
            {
                switch (a[1])
                {
                    case 'r': ParseRedundancy(a, opt); break;
                    case 'c':
                    {
                        long n = ParseNumber(a, 2, out bool ok);
                        if (!ok) { Error("Invalid recovery block count option."); return 1; }
                        opt.RecoveryBlockCount = (int)n;
                        break;
                    }
                    case 's':
                    {
                        long n = ParseNumber(a, 2, out bool ok);
                        if (!ok) { Error("Invalid block size option."); return 1; }
                        opt.BlockSize = n;
                        break;
                    }
                    case 'b':
                    {
                        long n = ParseNumber(a, 2, out bool ok);
                        if (!ok) { Error("Invalid block count option."); return 1; }
                        opt.BlockSize = 0; // block count not directly supported; use size
                        Console.Error.WriteLine($"Warning: -b (block count) not supported; ignoring.");
                        break;
                    }
                    case 'f':
                    {
                        long n = ParseNumber(a, 2, out bool ok);
                        if (!ok) { Error("Invalid first block option."); return 1; }
                        opt.FirstRecoveryBlock = (int)n;
                        break;
                    }
                    case 'n':
                    {
                        long n = ParseNumber(a, 2, out bool ok);
                        if (!ok) { Error("Invalid recovery file count option."); return 1; }
                        opt.RecoveryFileCount = (int)n;
                        break;
                    }
                    case 'u':
                        opt.Scheme = RecoveryFileScheme.Uniform;
                        break;
                    case 'l':
                        opt.Scheme = RecoveryFileScheme.Limited;
                        break;
                    case 'q':
                        opt.Quiet = true;
                        break;
                    default:
                        Error($"Unknown option: {a}");
                        return 1;
                }
            }
            else
            {
                if (parName == null)
                    parName = a;
                else
                    files.Add(a);
            }
        }

        if (parName == null)
        {
            Error("A PAR2 file name is required.");
            PrintUsage();
            return 1;
        }

        // Expand wildcards / globs and allow a directory.
        files = ExpandFiles(files);
        if (files.Count == 0)
        {
            Error("No source files to protect.");
            return 1;
        }

        var result = Par2Writer.CreateAsync(opt, parName, files.ToArray()).GetAwaiter().GetResult();
        Console.WriteLine($"Set ID: {result.SetId}");
        Console.WriteLine("Done.");
        return 0;
    }

    private static int DoVerify(string[] args, bool repair)
    {
        var opt = new CreateOptions { Log = Console.WriteLine };
        string? parName = null;
        var files = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("-"))
            {
                if (a[1] == 'q')
                    opt.Quiet = true;
                continue;
            }
            if (parName == null)
                parName = a;
            else
                files.Add(a);
        }

        if (parName == null)
        {
            Error("A PAR2 file name is required.");
            PrintUsage();
            return 1;
        }

        string baseName = Path.GetFileNameWithoutExtension(parName);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(parName));
        if (string.IsNullOrEmpty(dir))
            dir = Environment.CurrentDirectory;

        if (!File.Exists(parName))
        {
            Error($"Cannot find {parName}.");
            return 1;
        }

        var scan = Par2Scanner.ScanDirectory(dir);
        var set = scan.Sets.FirstOrDefault(s => s.SetId == SetIdOf(parName)) ?? scan.Sets.FirstOrDefault();
        if (set == null || !set.HasMain)
        {
            Error("No valid PAR2 recovery set found for this file.");
            return 1;
        }

        var repairer = new Par2Repairer(Console.WriteLine, opt.Quiet);
        var report = repairer.RunAsync(set, dir, repair).GetAwaiter().GetResult();

        foreach (var f in report.Files)
        {
            Console.WriteLine($"  {f.Name} - {f.Problem}");
        }
        Console.WriteLine(report.Message);

        _exitCode = report.Status switch
        {
            RepairStatus.Ok or RepairStatus.Repaired => 0,
            _ => 1
        };
        return _exitCode;
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
                if (packet == null)
                    break;
                if (packet.Kind == Par2PacketKind.Main)
                    return packet.SetId.ToString();
                offset += (int)packet.Length;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    // ---- Argument helpers ----

    private static void ParseRedundancy(string a, CreateOptions opt)
    {
        int i = 2;
        char suffix = i < a.Length ? a[i] : '\0';
        if (suffix == 'k' || suffix == 'm' || suffix == 'g')
        {
            i++;
            long sizeValue = 0;
            while (i < a.Length && char.IsDigit(a[i]))
            {
                sizeValue = sizeValue * 10 + (a[i] - '0');
                i++;
            }
            if (suffix == 'g') sizeValue *= 1024 * 1024 * 1024;
            else if (suffix == 'm') sizeValue *= 1024 * 1024;
            else sizeValue *= 1024;
            // Convert a byte size to a recovery block count once the block size is
            // known; approximate using default and let the writer handle it.
            Console.Error.WriteLine($"Warning: redundancy by size (-r{a[2..]}) not supported; using -c approximation skipped.");
            return;
        }

        long pctValue = 0;
        bool any = false;
        while (i < a.Length && char.IsDigit(a[i]))
        {
            pctValue = pctValue * 10 + (a[i] - '0');
            i++;
            any = true;
        }
        if (!any)
            return;
        opt.RedundancyPercent = (int)pctValue;
    }

    private static long ParseNumber(string a, int start, out bool ok)
    {
        ok = false;
        long value = 0;
        bool any = false;
        for (int i = start; i < a.Length; i++)
        {
            if (!char.IsDigit(a[i]))
                break;
            value = value * 10 + (a[i] - '0');
            any = true;
        }
        ok = any;
        return value;
    }

    private static List<string> ExpandFiles(List<string> input)
    {
        var result = new List<string>();
        foreach (var f in input)
        {
            if (Directory.Exists(f))
            {
                result.AddRange(Directory.EnumerateFiles(f)
                    .Where(x => !x.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)));
            }
            else if (f.Contains("*") || f.Contains("?"))
            {
                var dir = Path.GetDirectoryName(f) ?? ".";
                var pat = Path.GetFileName(f);
                result.AddRange(Directory.EnumerateFiles(dir, pat)
                    .Where(x => !x.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)));
            }
            else
            {
                result.Add(f);
            }
        }
        return result.Distinct().ToList();
    }

    private static void Error(string message) => Console.Error.WriteLine(message);

    private static int PrintVersion()
    {
        Console.WriteLine("par2 (ParPar) version 1.0");
        Console.WriteLine("A PAR 2.0 compatible recovery volume tool. Compatible with QuickPar.");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(@"Usage:
  par2 c(reate) [options] <PAR2 file> [files] : Create PAR2 files
  par2 v(erify) [options] <PAR2 file> [files] : Verify files using PAR2 file
  par2 r(epair) [options] <PAR2 file> [files] : Repair files using PAR2 files
  par2 monitor [options] <directory>          : Watch a directory and auto-repair

Create options:
  -r<number>          Set the amount of redundancy required (percent, e.g. -r20)
  -c<number>          Set the number of recovery blocks to create
  -s<number>          Set the block size in bytes (multiple of 4)
  -f<number>          Specify the first recovery block number
  -n<number>          Specify the number of recovery files
  -u                  Specify uniformly sized recovery files
  -l                  Limit the size of the recovery files
  -q                  Set the output to quiet

Monitor options:
  -t<number>          Seconds between scans (default 5)
  -k                  Keep the set's .par2 files after a successful repair
                      (by default they are deleted once the set is verified
                      or repaired).
                      A set is repaired as soon as enough recovery blocks are
                      present; a set that is still incomplete is checked again
                      on later scans, and the monitor moves on to the next set
                      in the meantime (no percentage setting).

If [files] are not given, the current directory is scanned for non-.par2 files.");
    }
}
