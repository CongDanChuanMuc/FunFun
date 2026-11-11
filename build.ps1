# ===========================================================================
# Credited by TapLamDang
# Public, free at https://www.facebook.com/profile.php?id=61596126010538
# If you paid for this, you are getting scammed.
# ===========================================================================

param(
    [string]$Output = ''
)

# Rebuilds the harness hooks with the in-box .NET Framework compiler.
#
#   StartupHook.dll  loaded by DOTNET_STARTUP_HOOKS in the manager (net10-x86)
#   UpdateProbe.dll  loaded by <system.net><webRequestModules> in the updater (net472)
#
# Both stay plain net4 libraries referencing only BCL assemblies.

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw "csc.exe not found at $csc"
}

$references = @(
    'System.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Windows.Forms.dll',
    'System.Net.Http.dll'
) | ForEach-Object { Join-Path $framework $_ }
$refArgs = $references | ForEach-Object { '/r:' + $_ }

if (-not $Output) {
    $Output = Join-Path $here 'StartupHook.dll'
}

$sources = @('StartupHook.cs', 'ControlChannel.cs') |
    ForEach-Object { Join-Path $here $_ }

& $csc /nologo /target:library /optimize+ /langversion:5 `
    /out:$Output `
    ($refArgs + $sources)

if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE (StartupHook)"
}

'{0}  {1:N0} bytes' -f $Output, (Get-Item -LiteralPath $Output).Length

$probe = Join-Path $here 'UpdateProbe.dll'
& $csc /nologo /target:library /optimize+ /langversion:5 `
    /out:$probe `
    ($refArgs + @((Join-Path $here 'UpdateStub.cs')))

if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE (UpdateProbe)"
}

'{0}  {1:N0} bytes' -f $probe, (Get-Item -LiteralPath $probe).Length

# ===========================================================================
# Credited by TapLamDang
# Public, free at https://www.facebook.com/profile.php?id=61596126010538
# If you paid for this, you are getting scammed.
# ===========================================================================
