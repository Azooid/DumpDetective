<#
.SYNOPSIS
    Triggers every DumpDetective DiagnosticScenario endpoint and automatically
    collects a named memory dump for each one.

.DESCRIPTION
    For each DumpDetective command there is a matching scenario endpoint in the
    running DiagnosticScenarios web app.  This script:

      1. Locates or starts the DiagnosticScenarios process.
      2. Calls each scenario API endpoint to activate the condition.
      3. Waits the appropriate settle time (longer for thread/async scenarios).
      4. Takes a full process dump using dotnet-dump (preferred) or procdump.
      5. Names the .dmp file after the DumpDetective command so you can run:
             DumpDetective <command> C:\DumpDetective\Dumps\<command>\*.dmp
      6. Optionally registers Windows Debug Diagnostic 2.x rules via COM so
         that DebugDiag can also collect dumps automatically in the background.

.PARAMETER BaseUrl
    Base URL of the running DiagnosticScenarios app.
    Default: http://localhost:5121

.PARAMETER DumpRoot
    Root folder for all dumps.
    Default: C:\DumpDetective\Dumps

.PARAMETER DumpTool
    Which tool to use for dump collection: 'dotnet-dump', 'procdump', or 'auto'.
    'auto' tries dotnet-dump first, then procdump.
    Default: auto

.PARAMETER Scenarios
    Comma-separated list of scenarios to run.  Omit to run all of them.
    Example: -Scenarios "memory-leak,deadlock-detection,async-stacks"

.PARAMETER RegisterDebugDiagRules
    If set, attempts to register the .ddconfig rules via DebugDiag COM API.

.PARAMETER StartApp
    If set, starts the DiagnosticScenarios process before running scenarios.
    Requires dotnet to be on PATH.

.EXAMPLE
    # Run all scenarios, auto-detect dump tool
    .\Invoke-ScenarioDumps.ps1

.EXAMPLE
    # Run only the memory and thread scenarios
    .\Invoke-ScenarioDumps.ps1 -Scenarios "memory-leak,heap-stats,thread-pool-starvation,deadlock-detection"

.EXAMPLE
    # Start the app, register DebugDiag rules, then collect all dumps
    .\Invoke-ScenarioDumps.ps1 -StartApp -RegisterDebugDiagRules
#>
[CmdletBinding()]
param(
    [string]$BaseUrl                 = "http://localhost:5121",
    [string]$DumpRoot                = "C:\DumpDetective\Dumps",
    [ValidateSet("dotnet-dump","procdump","auto")]
    [string]$DumpTool                = "auto",
    [string]$Scenarios               = "",
    [switch]$RegisterDebugDiagRules,
    [switch]$StartApp
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Helpers ───────────────────────────────────────────────────────────────────

function Write-Step   ([string]$msg) { Write-Host "  ► $msg" -ForegroundColor Cyan }
function Write-Ok     ([string]$msg) { Write-Host "  ✓ $msg" -ForegroundColor Green }
function Write-Warn   ([string]$msg) { Write-Host "  ⚠ $msg" -ForegroundColor Yellow }
function Write-Header ([string]$msg) { Write-Host "`n━━━ $msg ━━━" -ForegroundColor Magenta }

function Invoke-Reset {
    try {
        $r = Invoke-RestMethod -Uri "$BaseUrl/api/diagscenario/reset" -Method Post -TimeoutSec 15 -DisableKeepAlive
        $msg = if ($r.message) { $r.message } else { 'reset acknowledged' }
        Write-Step "Reset: $msg"
    } catch {
        Write-Warn "Reset call failed: $_ (continuing)"
    }
}

# Retries POST /reset every 5 s for up to $TimeoutSeconds (default 60).
# If the server never responds (pool still saturated), kills the process and
# restarts it from the project directory so the next scenario starts clean.
# Returns $true = reset succeeded cleanly, $false = process was restarted.
function Invoke-ResetWithRetry {
    param([int]$TimeoutSeconds = 60)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $attempt  = 0

    while ((Get-Date) -lt $deadline) {
        $attempt++
        $remaining = [int](($deadline - (Get-Date)).TotalSeconds)
        Write-Step "Reset attempt $attempt  ($remaining s remaining)..."
        try {
            $r   = Invoke-RestMethod -Uri "$BaseUrl/api/diagscenario/reset" -Method Post -TimeoutSec 8 -DisableKeepAlive
            $msg = if ($r.message) { $r.message } else { 'reset acknowledged' }
            Write-Ok "Reset succeeded (attempt $attempt): $msg"
            return $true
        } catch {
            Write-Step "Reset not ready yet — retrying in 5 s..."
            Start-Sleep -Seconds 5
        }
    }

    # ── Fallback: pool never freed a thread in time — force restart ───────────
    Write-Warn "Reset did not respond within $TimeoutSeconds s.  Force-restarting the app..."
    $proc = Get-TargetProcess
    if ($proc) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Write-Step "Killed PID $($proc.Id)."
        Start-Sleep -Seconds 2
    }
    # Project folder is two levels above this script (…\Diagnostics\DebugDiag\)
    $projDir = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
    Write-Step "Restarting:  dotnet run --project $projDir --no-build"
    Start-Process "dotnet" -ArgumentList "run --project `"$projDir`" --no-build" -WindowStyle Normal
    Write-Step "Waiting 15 s for app to come back up..."
    Start-Sleep -Seconds 15
    Write-Ok "App restarted — ready for next scenario."
    return $false
}

function Invoke-Scenario([string]$endpoint, [string]$description) {
    Write-Step "Triggering: $description  →  $BaseUrl$endpoint"
    try {
        # -DisableKeepAlive: each call gets a fresh TCP connection so a previously
        # timed-out request on a stale keep-alive socket can never block this one.
        $resp = Invoke-RestMethod -Uri "$BaseUrl$endpoint" -Method Get -TimeoutSec 30 -DisableKeepAlive
        $msg = if ($resp.message) { $resp.message } else { $resp | ConvertTo-Json -Compress }
        Write-Ok "Response: $msg"
        return $true
    } catch {
        Write-Warn "Endpoint failed: $_"
        return $false
    }
}

function Get-TargetProcess {
    return Get-Process -Name "DumpDetective.DiagnosticScenarios" -ErrorAction SilentlyContinue |
           Select-Object -First 1
}

function Find-DotnetDump {
    $cmd = Get-Command "dotnet-dump" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # Check common global tool paths
    $paths = @(
        "$env:USERPROFILE\.dotnet\tools\dotnet-dump.exe",
        "C:\Program Files\dotnet\tools\dotnet-dump.exe"
    )
    foreach ($p in $paths) { if (Test-Path $p) { return $p } }
    return $null
}

function Find-ProcDump {
    $cmd = Get-Command "procdump" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $paths = @(
        "C:\Tools\procdump.exe",
        "C:\Sysinternals\procdump.exe",
        "$env:ProgramFiles\Sysinternals\procdump.exe"
    )
    foreach ($p in $paths) { if (Test-Path $p) { return $p } }
    return $null
}

function Take-Dump([int]$processId, [string]$outputPath, [string]$label) {
    $dir  = Split-Path $outputPath -Parent
    $null = New-Item -ItemType Directory -Path $dir -Force

    Write-Step "Collecting dump for '$label'  →  $outputPath"

    if ($script:dumpCmd -eq "dotnet-dump") {
        & $script:dumpExe collect --process-id $processId --output $outputPath --type Full 2>&1 | Out-Null
    } elseif ($script:dumpCmd -eq "procdump") {
        # -ma = full dump, -accepteula = no prompt
        & $script:dumpExe -ma $processId $outputPath -accepteula 2>&1 | Out-Null
    }

    if (Test-Path $outputPath) {
        $size = (Get-Item $outputPath).Length / 1MB
        Write-Ok "Dump saved: $outputPath  ($([math]::Round($size,1)) MB)"
        return $outputPath
    } else {
        Write-Warn "Dump file not found at expected path — check dump tool output."
        return $null
    }
}

# ── Scenario table ────────────────────────────────────────────────────────────
# Each entry: [slug, endpoint, settle-seconds, description]
$AllScenarios = @(
    # Heap & Memory
    [pscustomobject]@{ Slug="heap-stats";         Endpoint="/api/diagscenario/heap-stats";          Settle=5;  Desc="10 custom types × 500 instances on the managed heap" },
    [pscustomobject]@{ Slug="gen-summary";         Endpoint="/api/diagscenario/gen-summary";          Settle=8;  Desc="Objects promoted across Gen0/1/2 and LOH" },
    [pscustomobject]@{ Slug="large-objects";       Endpoint="/api/diagscenario/large-objects";        Settle=5;  Desc="50 × 200 KB byte arrays on the LOH" },
    [pscustomobject]@{ Slug="memory-leak";         Endpoint="/api/diagscenario/memory-leak";          Settle=3;  Desc="Appends 1 MB to a never-freed static list" },
    # GC
    [pscustomobject]@{ Slug="high-refs";           Endpoint="/api/diagscenario/high-refs";            Settle=5;  Desc="Hub object with 5 000 inbound references" },
    [pscustomobject]@{ Slug="heap-fragmentation";  Endpoint="/api/diagscenario/heap-fragmentation";   Settle=10; Desc="Alternating pinned/freed arrays leaving heap gaps" },
    [pscustomobject]@{ Slug="pinned-objects";      Endpoint="/api/diagscenario/pinned-objects";       Settle=5;  Desc="200 GCHandle.Pinned handles" },
    [pscustomobject]@{ Slug="gc-roots";            Endpoint="/api/diagscenario/gc-roots";             Settle=5;  Desc="Static + GCHandle object roots" },
    [pscustomobject]@{ Slug="finalizer-queue";     Endpoint="/api/diagscenario/finalizer-queue";      Settle=8;  Desc="500 finalizable objects + blocked finalizer thread" },
    [pscustomobject]@{ Slug="handle-table";        Endpoint="/api/diagscenario/handle-table";         Settle=5;  Desc="300 GCHandles (Normal / Weak / WeakTrackResurrection)" },
    [pscustomobject]@{ Slug="static-refs";         Endpoint="/api/diagscenario/static-refs";          Settle=5;  Desc="Large object graph anchored to static fields" },
    [pscustomobject]@{ Slug="weak-refs";           Endpoint="/api/diagscenario/weak-refs";            Settle=5;  Desc="1 000 WeakReference<T> objects" },
    # Strings
    [pscustomobject]@{ Slug="string-duplicates";   Endpoint="/api/diagscenario/string-duplicates";    Settle=5;  Desc="3 000 uninterned duplicate strings" },
    # Threads
    [pscustomobject]@{ Slug="thread-analysis";     Endpoint="/api/diagscenario/thread-analysis";      Settle=5;  Desc="20 named threads blocked on a wait handle" },
    [pscustomobject]@{ Slug="thread-pool";            Endpoint="/api/diagscenario/thread-pool";              Settle=30; HangOnTrigger=$true;  Desc="80 thread-pool work items sleeping" },
    [pscustomobject]@{ Slug="thread-pool-starvation"; Endpoint="/api/diagscenario/thread-pool-starvation";  Settle=35; HangOnTrigger=$true;  Desc="Sync-over-async blocking saturating the pool" },
    [pscustomobject]@{ Slug="deadlock-detection";  Endpoint="/api/diagscenario/deadlock-detection";   Settle=8;  HangOnTrigger=$true; Desc="Two named threads in a classic lock deadlock" },
    # Async & HTTP
    [pscustomobject]@{ Slug="async-stacks";        Endpoint="/api/diagscenario/async-stacks";         Settle=8;  Desc="100 suspended async state machines on the heap" },
    [pscustomobject]@{ Slug="http-requests";       Endpoint="/api/diagscenario/http-requests";        Settle=5;  Desc="Leaked HttpClient + stalled HttpRequestMessage objects" },
    # Exceptions & Events
    [pscustomobject]@{ Slug="exception-analysis";  Endpoint="/api/diagscenario/exception-analysis";   Settle=5;  Desc="200 Exception instances across 5 types" },
    [pscustomobject]@{ Slug="event-analysis";      Endpoint="/api/diagscenario/event-analysis";       Settle=5;  Desc="500 lambda subscribers on a static event (never removed)" },
    # Connections & Timers
    [pscustomobject]@{ Slug="connection-pool";     Endpoint="/api/diagscenario/connection-pool";      Settle=5;  Desc="100 undisposed SqlConnection objects" },
    [pscustomobject]@{ Slug="wcf-channels";        Endpoint="/api/diagscenario/wcf-channels";         Settle=5;  Desc="50 System.ServiceModel channel objects (15 faulted)" },
    [pscustomobject]@{ Slug="timer-leaks";         Endpoint="/api/diagscenario/timer-leaks";          Settle=5;  Desc="300 System.Threading.Timer instances never disposed" },
    # Types
    [pscustomobject]@{ Slug="type-instances";      Endpoint="/api/diagscenario/type-instances";       Settle=5;  Desc="1 000 TargetObject instances" },
    [pscustomobject]@{ Slug="object-inspect";      Endpoint="/api/diagscenario/object-inspect";       Settle=3;  Desc="Single InspectableObject with known field values" }
)

# ── Resolve dump tool ─────────────────────────────────────────────────────────

$script:dumpExe = $null
$script:dumpCmd = $null

if ($DumpTool -in "dotnet-dump","auto") {
    $exe = Find-DotnetDump
    if ($exe) { $script:dumpExe = $exe; $script:dumpCmd = "dotnet-dump" }
    elseif ($DumpTool -eq "dotnet-dump") {
        Write-Error "dotnet-dump not found. Install it with: dotnet tool install --global dotnet-dump"
    }
}
if (-not $script:dumpExe -and $DumpTool -in "procdump","auto") {
    $exe = Find-ProcDump
    if ($exe) { $script:dumpExe = $exe; $script:dumpCmd = "procdump" }
}
if (-not $script:dumpExe) {
    Write-Error @"
No dump tool found.  Install one of:
  dotnet-dump  →  dotnet tool install --global dotnet-dump
  ProcDump     →  winget install Microsoft.Sysinternals.ProcDump
               →  or download from https://learn.microsoft.com/sysinternals/downloads/procdump
"@
}

Write-Ok "Dump tool: $script:dumpCmd  ($script:dumpExe)"

# ── Optionally start the app ──────────────────────────────────────────────────

if ($StartApp) {
    Write-Header "Starting DiagnosticScenarios"
    $projDir = Join-Path $PSScriptRoot "..\.." | Resolve-Path
    $proc = Get-TargetProcess
    if ($proc) {
        Write-Warn "Process already running (PID $($proc.Id)) — skipping start."
    } else {
        Write-Step "dotnet run --project $projDir"
        Start-Process "dotnet" -ArgumentList "run --project `"$projDir`"" -WindowStyle Normal
        Write-Step "Waiting 15 s for ASP.NET Core startup..."
        Start-Sleep -Seconds 15
    }
}

# ── Wait for the process ──────────────────────────────────────────────────────

Write-Header "Locating Target Process"
$targetProc = Get-TargetProcess
if (-not $targetProc) {
    Write-Error @"
Process 'DumpDetective.DiagnosticScenarios' not found.
Start the app first:  dotnet run --project DumpDetective.DiagnosticScenarios\
Or pass -StartApp to this script.
"@
}
$mainModule = if ($targetProc.MainModule) { $targetProc.MainModule.FileName } else { 'n/a' }
Write-Ok "Found PID $($targetProc.Id)  —  $mainModule"

# Verify HTTP connectivity
Write-Step "Checking HTTP connectivity at $BaseUrl ..."
try {
    $null = Invoke-RestMethod -Uri "$BaseUrl/" -Method Get -TimeoutSec 10 -ContentType "text/html"
    Write-Ok "App is reachable."
} catch {
    Write-Warn "Landing page check failed ($_). Continuing anyway — JSON endpoints may still work."
}

# ── Filter to requested scenarios ─────────────────────────────────────────────

$runList = @(if ($Scenarios) {
    $slugs = @($Scenarios -split "," | ForEach-Object { $_.Trim() })
    $AllScenarios | Where-Object { $_.Slug -in $slugs }
} else {
    $AllScenarios
})

Write-Header "Running $($runList.Count) Scenario(s)"

Write-Step "Resetting app state before run..."
Invoke-Reset

$results = [System.Collections.Generic.List[pscustomobject]]::new()

foreach ($s in $runList) {
    Write-Header "$($s.Slug.ToUpper())"
    Write-Host "  $($s.Desc)" -ForegroundColor Gray

    # ── Hanging-request scenarios (thread-pool, thread-pool-starvation) ──────
    # The endpoint intentionally blocks until Reset() is called.
    # We fire the trigger in a background job, take the dump while it hangs,
    # then call Reset() which signals/cancels the server and closes the conn.
    $hangOnTrigger = [bool]($s.PSObject.Properties['HangOnTrigger'] -and $s.HangOnTrigger)
    if ($hangOnTrigger) {
        $triggerUrl = "$BaseUrl$($s.Endpoint)"
        Write-Step "Firing background trigger (endpoint will hang — that is expected)..."
        $triggerJob = Start-Job -ScriptBlock {
            param([string]$url)
            try   { Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 600 -DisableKeepAlive }
            catch { } # timeout / cancellation from the server side — ignored
        } -ArgumentList $triggerUrl

        # Give the pool a moment to start saturating, then settle
        Start-Sleep -Seconds 3
        Write-Ok "Background trigger fired — pool is saturating..."
        Write-Step "Settling for $($s.Settle) second(s)..."
        Start-Sleep -Seconds $s.Settle

        $targetProc = Get-TargetProcess
        if (-not $targetProc) {
            Write-Warn "Process disappeared — skipping dump."
            $results.Add([pscustomobject]@{ Scenario=$s.Slug; Status="SKIP (process gone)"; DumpPath="" })
            Stop-Job  $triggerJob -ErrorAction SilentlyContinue
            Remove-Job $triggerJob -Force
            continue
        }

        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $dumpFile  = Join-Path $DumpRoot "$($s.Slug)\$($s.Slug)-$timestamp.dmp"
        $dumpPath  = Take-Dump -processId $targetProc.Id -outputPath $dumpFile -label $s.Slug

        # Reset() signals the gate / cancels the CTS — unblocks all pool threads
        # AND the hanging handler, so Kestrel can close the connection cleanly.
        # Retry for up to 60 s; if the pool never frees a thread, restart the app.
        Write-Step "Calling Reset() (will wait up to 60 s for pool threads to free)..."
        $resetClean = Invoke-ResetWithRetry -TimeoutSeconds 60

        if ($resetClean) {
            # Server closed connection cleanly — background job should finish quickly
            $null = Wait-Job $triggerJob -Timeout 20
        }
        Remove-Job $triggerJob -Force -ErrorAction SilentlyContinue

        $results.Add([pscustomobject]@{
            Scenario = $s.Slug
            Status   = if ($dumpPath) { "OK" } else { "DUMP FAILED" }
            DumpPath = if ($dumpPath) { $dumpPath } else { "" }
        })
        Start-Sleep -Seconds 2
        continue
    }

    # ── Normal scenarios ───────────────────────────────────────────────────────
    $triggered = Invoke-Scenario -endpoint $s.Endpoint -description $s.Desc
    if (-not $triggered) {
        $results.Add([pscustomobject]@{ Scenario=$s.Slug; Status="SKIP (endpoint error)"; DumpPath="" })
        continue
    }

    Write-Step "Settling for $($s.Settle) second(s)..."
    Start-Sleep -Seconds $s.Settle

    # Re-resolve PID in case of restart
    $targetProc = Get-TargetProcess
    if (-not $targetProc) {
        Write-Warn "Process disappeared — skipping dump."
        $results.Add([pscustomobject]@{ Scenario=$s.Slug; Status="SKIP (process gone)"; DumpPath="" })
        continue
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $dumpFile  = Join-Path $DumpRoot "$($s.Slug)\$($s.Slug)-$timestamp.dmp"
    $dumpPath  = Take-Dump -processId $targetProc.Id -outputPath $dumpFile -label $s.Slug

    $results.Add([pscustomobject]@{
        Scenario = $s.Slug
        Status   = if ($dumpPath) { "OK" } else { "DUMP FAILED" }
        DumpPath = if ($dumpPath) { $dumpPath } else { "" }
    })

    # Reset all scenario state so the next run starts from a clean heap
    Write-Step "Resetting app state after '$($s.Slug)'..."
    Invoke-Reset
    Start-Sleep -Seconds 2
}

# ── Optionally register DebugDiag rules ───────────────────────────────────────

if ($RegisterDebugDiagRules) {
    Write-Header "Registering Windows Debug Diagnostic Rules"

    $ddconfigPath = Join-Path $PSScriptRoot "DumpDetective-Scenarios.ddconfig"
    if (-not (Test-Path $ddconfigPath)) {
        Write-Warn "ddconfig not found at $ddconfigPath — skipping DebugDiag registration."
    } else {
        try {
            # DebugDiag 2.x exposes a COM controller for automation
            $dd = New-Object -ComObject "DebugDiag.DbgCtrl" -ErrorAction Stop
            Write-Ok "DebugDiag COM object created."

            # Build the dump output directories
            $ruleMap = @{
                "DiagScenarios — Memory Leak (300 MB)"           = "memory-leak"
                "DiagScenarios — Large Object Heap Pressure (500 MB)" = "large-objects"
                "DiagScenarios — Thread Pool Saturation (>80 threads)" = "thread-pool"
                "DiagScenarios — CLR First-Chance Exception"     = "exception-analysis"
                "DiagScenarios — Process Hang / Deadlock (>30 s)" = "deadlock"
                "DiagScenarios — Baseline on Process Start"      = "baseline"
            }

            foreach ($rname in $ruleMap.Keys) {
                $subDir = Join-Path $DumpRoot $ruleMap[$rname]
                $null   = New-Item -ItemType Directory -Path $subDir -Force
            }

            Write-Step "Importing rules from $ddconfigPath ..."
            # DebugDiag 2.x import method (may vary by installed version)
            if ($dd | Get-Member -Name "ImportRules" -ErrorAction SilentlyContinue) {
                $dd.ImportRules($ddconfigPath)
                Write-Ok "Rules imported successfully."
            } else {
                Write-Warn "ImportRules method not found on this DebugDiag version."
                Write-Warn "Manual import: open DebugDiag 2.x → Tools → Import Rules → $ddconfigPath"
            }

            [System.Runtime.InteropServices.Marshal]::ReleaseComObject($dd) | Out-Null
        } catch [System.Runtime.InteropServices.COMException] {
            Write-Warn "DebugDiag COM object unavailable (is DebugDiag 2.x installed?)."
            Write-Warn "Manual import: open DebugDiag 2.x → Tools → Import Rules → $ddconfigPath"
        } catch {
            Write-Warn "DebugDiag registration error: $_"
        }
    }
}

# ── Summary ───────────────────────────────────────────────────────────────────

Write-Header "Results"
$results | Format-Table -AutoSize

# @() ensures an array even when Where-Object returns $null (zero matches)
$successful = @($results | Where-Object { $_.Status -eq "OK" })
Write-Host ""
Write-Ok "$($successful.Count) of $($results.Count) dumps collected."
Write-Host ""
Write-Host "  Run DumpDetective against each dump:" -ForegroundColor White
foreach ($r in $successful) {
    Write-Host "    DumpDetective $($r.Scenario) `"$($r.DumpPath)`"" -ForegroundColor Gray
}

# ── Trend analysis hint ───────────────────────────────────────────────────────

if ($successful.Count -gt 1) {
    Write-Host ""
    Write-Host "  Trend analysis across all dumps:" -ForegroundColor White
    Write-Host "    DumpDetective trend-analysis `"$DumpRoot`"" -ForegroundColor Gray
    Write-Host "    DumpDetective trend-render    `"$DumpRoot`"" -ForegroundColor Gray
}
