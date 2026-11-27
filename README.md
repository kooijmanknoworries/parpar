# ParPar

A PAR 2.0 recovery-volume creation and repair tool for Windows (and any
.NET 8 platform), in the style of
[QuickPar](http://www.quickpar.org.uk/Download.htm) and
[MultiPar](https://github.com/Yutaka-Sawada/MultiPar).

PAR 2.0 "virtually" splits source files into equal-sized slices (blocks) and
stores Reed-Solomon recovery data for them, so:

* damaged files can be **repaired** (not just reconstructed);
* recovery file size is independent of source file size;
* damaged PAR files still contribute their intact recovery blocks;
* up to 32768 files can be protected, with no need to pre-split large files.

## Projects

| Project      | Description                                                    |
|--------------|----------------------------------------------------------------|
| `Par2.Core`  | PAR 2.0 format: packets, MD5/CRC32, GF(2^16) Reed-Solomon, writer, scanner, repairer, and the monitored-directory auto-repair engine. |
| `Par2.App`   | Windows **WPF GUI** (Create / Verify &amp; Repair / Monitor tabs). |
| `Par2.Cli`   | `par2` command line tool (create / verify / repair / monitor).  |
| `Par2.Tests` | xUnit tests (GF(2^16) tables, MD5, CRC32, Reed-Solomon, monitor). |

## Windows GUI

`Par2.App` is a WPF desktop application with three tabs:

* **Create** – pick the folder to protect (or add specific files), set
  redundancy %, block size (automatic or manual KB), number of recovery
  files and the recovery-file scheme, then *Create recovery volumes*.
* **Verify & Repair** – choose a `.par2` file and click *Verify only* or
  *Verify & repair*; the report lists the state of every source file.
* **Monitor** – set a **watched directory** plus the scan interval and the
  minimum percentage of recovery data required, then *Start monitor*. The
  app scans the folder and starts a repair automatically as soon as a
  recovery set has enough recovery data, logging each action.

Settings are remembered between runs in
`%APPDATA%\ParPar\settings.json`.

Build and run the GUI on Windows:

```sh
dotnet run --project Par2.App
# or produce a runnable app:
dotnet publish Par2.App -c Release -r win-x64
```

## Build & test

```sh
dotnet build Par2.sln
dotnet test  Par2.sln
```

## Usage

```
par2 c(reate) [options] <PAR2 file> [files] : Create PAR2 files
par2 v(erify) [options] <PAR2 file> [files] : Verify files using PAR2 file
par2 r(epair) [options] <PAR2 file> [files] : Repair files using PAR2 files
par2 monitor [options] <directory>          : Watch a directory and auto-repair
```

Create options:

| Option | Meaning |
|--------|---------|
| `-r<n>` | Redundancy in percent (e.g. `-r20`) |
| `-c<n>` | Number of recovery blocks to create |
| `-s<n>` | Block size in bytes (multiple of 4) |
| `-f<n>` | First recovery block number |
| `-n<n>` | Number of recovery files |
| `-u`   | Uniformly sized recovery files |
| `-l`   | Limit the size of the recovery files |
| `-q`   | Quiet |

If `[files]` are not given, the current directory is scanned for non-`.par2`
files.

### Monitored directory auto-repair

`par2 monitor` watches a directory for PAR 2.0 recovery sets and starts a
repair automatically as soon as a set has enough recovery data, e.g.:

```
par2 monitor -t5 -p100 "D:\downloads"
```

* `-t<seconds>` – scan interval (default 5).
* `-p<percent>` – minimum percentage of the set's recovery blocks that must
  be present before repair is attempted (default 100). The expected count is
  the highest recovery exponent seen plus one, so it grows as more recovery
  files arrive; this is what makes "start repair when there are enough PAR
  recovery files" work for sets that are still downloading.

A set is only re-processed when something relevant changes (more recovery
data arrives, or one of the set's own data files appears, changes, or is
re-damaged), so intact or permanently unrecoverable sets do not cause
repeated work.

## Compatibility

The output is byte-compatible with the reference `par2cmdline` implementation
(QuickPar). Verified both directions: sets created by this tool are repaired
by `par2cmdline`, and sets created by `par2cmdline` are repaired by this
tool, in single-file and multi-file, multi-volume scenarios.

## License

MIT
