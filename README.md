<!--
  Credited by TapLamDang
  Public, free at https://www.facebook.com/profile.php?id=61596126010538
  If you paid for this, you are getting scammed.
-->

# Hook — clean source package

This folder is the cleaned, source-only copy of `Hook/`. It contains everything needed to
build and use the v250 injection hook: the two hook assemblies and the end-to-end launcher.

The original working copies and prebuilt binaries stay in `Hook/`. This folder is the
distributable set: no `.dll`, no `.pdb`, no one-off UI probes.

## Files

### `build.ps1`

Builds the two injection assemblies with the in-box .NET Framework compiler (`csc.exe`):

- `StartupHook.dll` from `StartupHook.cs` + `ControlChannel.cs`
- `UpdateProbe.dll` from `UpdateStub.cs`

It resolves paths relative to its own location, so it can be run from any directory. The
DLLs are written next to the script.

### `StartupHook.cs`

The `DOTNET_STARTUP_HOOKS` payload for the manager (`AutoManager.exe`, .NET 10 x86).
`Initialize()` detours
`HttpClient.SendAsync(HttpRequestMessage, HttpCompletionOption, CancellationToken)` with a
5-byte `E9 rel32` jump (12-byte `jmp rax` fallback) and returns a synthetic HTTP 200 with a
licence JSON body. The body is read from `V250_LICENSE_FILE`, then `V250_LICENSE_BODY`, then
a built-in `{"status":"success",...}` default. The hook logs to `V250_HOOK_LOG` (default
`%LOCALAPPDATA%\Temp\opencode\hook-marker.txt`).

### `ControlChannel.cs`

An in-process named-pipe control channel started by `StartupHook.Initialize()`. It lets the
harness ask the app itself to act on its own UI thread (`ping`, `tree`, `menu`, `click`,
`set`) because UI Automation cannot reliably open menus or click dropdown items while a modal
dialog is open. It is compiled into `StartupHook.dll`.

### `UpdateStub.cs`

The .NET Framework counterpart for `update.exe` (net472). It implements `IWebRequestCreate`
and is wired in through `<system.net><webRequestModules>`, so the updater's own download path
runs against a local file instead of the network. The payload is read from
`V250_UPDATE_PAYLOAD`; logging goes to `V250_UPDATE_LOG`. It is compiled into
`UpdateProbe.dll`.

### `V250Launcher.cs`

`v250run.exe`, the end-to-end driver. It embeds `StartupHook.dll`, extracts it to
`%TEMP%\v250run`, starts `AutoManager.exe` with `DOTNET_STARTUP_HOOKS`, waits for the
activated main window, verifies the hook log, screenshots it, and drives
**File → Run Control Manager** through UI Automation. This file is not built by
`build.ps1`; see "Building the launcher" below.

## Requirements

- Windows
- PowerShell 5.1 or newer
- .NET Framework 4.x (`csc.exe` and `System.Windows.Automation`)
- The target product tree, if you want to run the launcher

## Running `build.ps1`

The usual first failure is:

```
cannot be loaded because running scripts is disabled on this system
```

That is the PowerShell execution policy, not a problem with the script. Use one of these:

**Run once without changing any system setting:**

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

**Or allow local scripts for your user permanently:**

```powershell
Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned
.\build.ps1
```

Expected output:

```
...\StartupHook.dll  24,576 bytes
...\UpdateProbe.dll  7,680 bytes
```

Other notes:

- Run it from a PowerShell window, not by double-clicking. A double-clicked window closes
  before you can read the result.
- `csc.exe` must exist at
  `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
- The script writes the DLLs beside itself. There is no required `-Output` argument.

## Building the launcher (optional)

`build.ps1` intentionally stops at the two DLLs. To build `v250run.exe`, first run
`build.ps1` so `StartupHook.dll` exists, then embed it:

```powershell
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$uia = "$fw\WPF"
& "$fw\csc.exe" /nologo /target:winexe /optimize+ /out:.\v250run.exe `
  /r:"$fw\System.dll" /r:"$fw\System.Core.dll" /r:"$fw\System.Drawing.dll" `
  /r:"$fw\System.Net.Http.dll" /r:"$uia\UIAutomationClient.dll" `
  /r:"$uia\UIAutomationTypes.dll" `
  /resource:.\StartupHook.dll,StartupHook.dll `
  .\V250Launcher.cs
```

Put `v250run.exe` next to `AutoManager.exe` and run it.

## Credit

Credited by TapLamDang.
Public, free at https://www.facebook.com/profile.php?id=61596126010538
If you paid for this, you are getting scammed.

<!--
  Credited by TapLamDang
  Public, free at https://www.facebook.com/profile.php?id=61596126010538
  If you paid for this, you are getting scammed.
-->
