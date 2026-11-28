# ParPar — project memory

## Status
- v1.0.0 released 2026-11-28. GitHub release with `parpar-1.0.0-win-x64.exe`
  (68.3 MB, self-contained single-file, zero-install) attached.
  Release: https://github.com/kooijmanknoworries/parpar/releases/tag/v1.0.0
  Tag v1.0.0 points at commit 36e95f7 (== main).
- Build: 0 warnings / 0 errors. Tests: 30/30 passing.
- Exe tested on the Windows 11 VM: GUI window "ParPar - PAR 2.0 recovery
  volumes" opens and stays alive, no runtime installed on the VM.

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

## Notes
- Build: `dotnet build Par2.sln -c Release`
- Test: `DOTNET_ROLL_FORWARD=Major dotnet test Par2.sln -c Release`
  (roll-forward needed: sandbox only has the .NET 10 runtime, tests target net8.0).
- Publish (no pwsh in sandbox — run the equivalent of publish.ps1 directly):
  `dotnet publish Par2.App/Par2.App.csproj -c Release -r win-x64 -p:DebugType=none -p:PublishSingleFile=true --self-contained true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist`
- Upload a release asset (GitHub API, token in the git remote URL):
  `curl -X POST -H "Authorization: Bearer $TOK" -H "Content-Type: application/octet-stream" --data-binary @dist/parpar.exe "<upload_url>?name=parpar-1.0.0-win-x64.exe"`
- Verify a GUI exe over SSH: `Start-Process -PassThru` reports MainWindowHandle=0
  (cross-session). Instead enumerate top-level windows by pid via user32
  EnumWindows + GetWindowThreadProcessId (see the vm_windowtest.ps1 pattern).
