# Builds tgmcp and the tray app from this branch (local/install) and installs
# them over %LOCALAPPDATA%\Programs\tgmcp, so this PC always runs upstream
# plus the away filter (away.skip, feat/away-skip-chats).
#
# The away filter is kept in the fork only and is never sent upstream as a PR.
# Use this script instead of the release installer, which would drop it.
#
#   powershell -ExecutionPolicy Bypass -File scripts\install-local.ps1            # update from upstream, then install
#   powershell -ExecutionPolicy Bypass -File scripts\install-local.ps1 -NoUpdate  # install this branch as it is
#   powershell -ExecutionPolicy Bypass -File scripts\install-local.ps1 -NoPush    # don't push local/install to the fork

param([switch]$NoUpdate, [switch]$NoPush)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$feature = 'origin/feat/away-skip-chats'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\tgmcp'
$stage = Join-Path $env:TEMP 'tgmcp-local-stage'

function Run([string]$exe, [string[]]$argv) {
  & $exe @argv
  if ($LASTEXITCODE -ne 0) { throw "$exe $($argv -join ' ') failed ($LASTEXITCODE)" }
}

if ((git branch --show-current) -ne 'local/install') { throw 'Run this from the local/install checkout.' }
$dirty = git status --porcelain | Where-Object { $_ -notmatch ' bun\.lock$' }
if ($dirty) { throw "Commit or discard these first:`n$($dirty -join "`n")" }

if (-not $NoUpdate) {
  Run git @('fetch', '--quiet', 'upstream')
  Run git @('fetch', '--quiet', 'origin')
  Run git @('merge', '--no-edit', 'upstream/main')
  Run git @('merge', '--no-edit', $feature)
}

# The away filter must be in what gets installed, whatever upstream did.
git merge-base --is-ancestor $feature HEAD
if ($LASTEXITCODE -ne 0) { throw "$feature is not merged into local/install." }
if (-not (Select-String -Path (Join-Path $repo 'src\*\*.ts') -Pattern 'away skip list' -SimpleMatch -Quiet)) {
  throw 'The away filter (away.skip) is missing from src; not installing.'
}

# This PC's bun writes an older lockfile; keep the committed one.
Run bun @('install')
Run git @('checkout', '--quiet', '--', 'bun.lock')
Run bun @('run', 'typecheck')
Run bun @('test', '--timeout', '30000', 'test/away.test.ts', 'test/away-daemon.test.ts', 'test/away-responder.test.ts', 'test/away-dst.test.ts', 'test/config.test.ts', 'test/tray.test.ts')

$arm = $env:PROCESSOR_ARCHITECTURE -eq 'ARM64'
$bunTarget = if ($arm) { 'bun-windows-arm64' } else { 'bun-windows-x64' }
$rid = if ($arm) { 'win-arm64' } else { 'win-x64' }
$version = (Get-Content -Raw package.json | ConvertFrom-Json).version
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force $stage | Out-Null
Run bun @('build', 'src/cli.ts', '--compile', "--target=$bunTarget", '--outfile', (Join-Path $stage 'tgmcp.exe'))
Run dotnet @('publish', 'tray/App/TelegramMCPTray.csproj', '-c', 'Release', '-r', $rid, '--self-contained', 'true',
  '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
  '-p:DebugType=none', "-p:Version=$version", '-o', (Join-Path $stage 'tray'), '-nologo', '-v', 'quiet')
if (-not (Select-String -Path (Join-Path $stage 'tgmcp.exe') -Pattern 'away skip list' -SimpleMatch -Quiet)) {
  throw 'The built tgmcp.exe has no away filter; not installing.'
}

# Swap in the new build. The old one stays as *.prev.exe to roll back to.
Stop-Process -Name TelegramMCPTray -ErrorAction SilentlyContinue -Confirm:$false
Start-Sleep -Seconds 2
foreach ($name in 'tgmcp', 'TelegramMCPTray') {
  $exe = Join-Path $dest "$name.exe"
  if (Test-Path $exe) { Move-Item $exe (Join-Path $dest "$name.prev.exe") -Force }
}
Copy-Item (Join-Path $stage 'tgmcp.exe') $dest
Copy-Item (Join-Path $stage 'tray\TelegramMCPTray.exe') $dest

# run-daemon.ps1 starts the daemon again within 10 s, from the new exe.
Get-CimInstance Win32_Process -Filter "Name='tgmcp.exe'" |
  Where-Object { $_.CommandLine -match '\sdaemon' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Confirm:$false }
Start-Sleep -Seconds 12
Run (Join-Path $dest 'tgmcp.exe') @('menubar')

if (-not $NoPush) { Run git @('push', '--quiet', 'origin', 'local/install') }
Write-Host "Installed tgmcp $version from local/install $(git rev-parse --short HEAD), with the away filter."
