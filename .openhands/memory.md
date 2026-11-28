# ParPar — project memory

## Status
- **v1.1.0 released 2026-11-28** (monitor rework):
  https://github.com/kooijmanknoworries/parpar/releases/tag/v1.1.0
  — asset `parpar-1.1.0-win-x64.exe` (71.7 MB, self-contained single-file,
  zero-install), tag v1.1.0 == commit 3b06634 (== main).
  Exe smoke-tested on the VM (GUI opens and stays alive).
- v1.0.0 release: https://github.com/kooijmanknoworries/parpar/releases/tag/v1.0.0
  (tag v1.0.0 == commit 36e95f7).
- Build: 0 warnings / 0 errors. Tests: 30/30 passing.
- Exe tested on the Windows 11 VM: GUI window opens, stays alive, no runtime
  installed on the VM. **Monitor auto-start (`--monitor`) verified on the VM**:
  launched the GUI, it auto-started monitoring a damaged set, repaired the file
  byte-identical to the original, and deleted all `.par2` files.

## Monitor rework (the 2026-11-28 change set — unreleased)
The v1.0.0 monitor was re-scanning the whole directory every tick and loading
every recovery block into memory, then re-attempting repair forever on a set that
could not be completed — hence "memory growth + nothing happens". Rewritten:
- **Streaming scanner** (`Par2Scanner`): hashes packets on the fly; a
  `RecoverySet` now holds only block *locations* (file, offset, length), never
  the bytes. Unreadable files are reported per-file with a reason.
- **On-demand recovery reads** (`Par2Repairer.RecoveryPacketData`): recovery
  data is read from disk only when the RS solver needs it. Peak RSS for a full
  monitor pass over 300 MB of recovery data: ~47 MB (vs unbounded before).
- **Repair progress** (`RepairProgress` record, `Action<RepairProgress>?`
  callback on `Par2Repairer.RunAsync`, `MonitorOptions.RepairProgress`):
  WPF progress bar + label on both the Verify tab and the Monitor tab.
- **Monitor** (`Par2Monitor`): processes sets **sequentially**, one at a time;
  announces every scan with a summary (`N .par2 files; X ready, Y waiting,
  Z without main`); a set that has recovery but no main packet is announced;
  waits for more recovery blocks instead of retrying repair; skips work when the
  directory state fingerprint is unchanged (but still re-checks when a `.par2`
  file is added/removed); **deletes all of a set's `.par2` files once the set is
  verified or repaired** (opt-out via `MonitorOptions.RemoveParFilesAfterRepair`
  / the "delete its .par2 files" checkbox / CLI `-k`).
- GUI: `--monitor` launch arg auto-starts the monitor (used to test the GUI over
  SSH and handy for unattended running). New UI: Monitor tab has a "Repair
  progress" group (bar + text) and a delete-after-repair checkbox; Verify tab
  has a progress bar.

## Decisions
- Distribute the binary as a **GitHub Release asset**, NOT in git (it's a 68 MB
  PE exe; `dist/` is gitignored). "Commit a release" here = tag v1.0.0 + GH release.
- Default publish = zero-install self-contained single-file (~68 MB). Users want
  to double-click and run with nothing installed (no .NET, no admin).
- WPF cross-compiles from Linux just fine: `EnableWindowsTargeting=true` is set
  in Par2.App.csproj; build with the .NET 10 SDK even though the app targets
  net8.0-windows.

## Open
- None known. Possible future: code-signing (currently unsigned; Windows Smart
  Screen will warn on first run until reputation builds), and a `.zip`/Inno
  Setup installer if the user wants one.
- Standing preference (user, 2026-11-28): for apps, ALWAYS commit + push +
  cut a release when work is done. Don't wait to be asked.

## Notes
- **Interop fixture data**: `Par2.Tests/TestData/*.par2` + `alpha.bin`/`beta.bin`
  are real PAR 2.0 files written by this app's own writer, and were verified
  byte-compatible with the reference `par2cmdline` (verify AND repair both
  directions, 2026-11-28). Regenerated from a throwaway console app; block size
  1024, 2 files x 8 blocks, 4 recovery blocks in 2 recovery files.
- **Monitor e2e test recipe** (CLI): damage one source block, then
  `par2 monitor -t1 -k <dir>`; expect scan summary, "repaired", "removed N
  .par2 file(s)", and the .par2 files gone; compare the repaired file to the
  saved original. With `-p50`/partial volumes, expect the "recovery data
  incomplete (x/y blocks)" wait line instead of a repair.
- Build: `dotnet build Par2.sln -c Release`
- Test: `DOTNET_ROLL_FORWARD=Major dotnet test Par2.sln -c Release`
  (roll-forward needed: sandbox only has the .NET 10 runtime, tests target net8.0).
- Publish (no pwsh in sandbox — run the equivalent of publish.ps1 directly):
  `dotnet publish Par2.App/Par2.App.csproj -c Release -r win-x64 -p:DebugType=none -p:PublishSingleFile=true --self-contained true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist`
- GitHub API (create release + upload asset): use the **`$GITHUB_TOKEN`
  environment variable** — the token embedded in the git remote URL does NOT
  work against the API (401 "Bad credentials"; it's a git-clone-only PAT).
  Create release: `curl -X POST -H "Authorization: Bearer $GITHUB_TOKEN" -H
  "Accept: application/vnd.github+json"
  https://api.github.com/repos/kooijmanknoworries/parpar/releases -d '{...}'`
  then upload to the returned `upload_url` with
  `-H "Content-Type: application/octet-stream" --data-binary @dist/parpar.exe`.
- Release recipe (v1.1.0, 2026-11-28): publish to `dist/` (above) → smoke-test
  the exe on the VM → `git tag vX.Y.Z && git push origin vX.Y.Z` → API release
  → upload `parpar-X.Y.Z-win-x64.exe` → update this memory + /memory/projects.md
  → commit + push the memory note.
- Verify a GUI exe over SSH: `Start-Process -PassThru` reports MainWindowHandle=0
  (cross-session). Instead enumerate top-level windows by pid via user32
  EnumWindows + GetWindowThreadProcessId (see the vm_windowtest.ps1 pattern).
