#Requires -Version 5.1
<#
.SYNOPSIS
    DevHubd operations helper: start, stop, status, supervise, drill.

.DESCRIPTION
    Wraps artifacts/publish/devhubd.exe (or -Exe) with a stable operational surface.
    ASCII-only on purpose: the file must stay UTF-8 without BOM, and Windows
    PowerShell 5.1 reads BOM-less files as ANSI. Non-ASCII text here would be garbled.

.PARAMETER Action
    start     Start the daemon (idempotent: reports an already healthy instance) and wait for /health.
    stop      Graceful stop via POST /internal/shutdown, then verify process and runtime.json are gone.
    status    Read runtime.json, ping /health and report the state.
    supervise Keep the daemon alive: restart within 1 second after a crash until a stop flag appears.
    drill     start -> kill the whole process tree -> supervisor restarts -> /health + MCP smoke.

.PARAMETER Exe
    Path to devhubd.exe. Defaults to <repo>/artifacts/publish/devhubd.exe.

.PARAMETER DataDir
    Data directory. Defaults to %LOCALAPPDATA%\userdrive-devhub (same default as the daemon).

.PARAMETER Port
    MCP port. Defaults to 3091, matching plugin/mcp.json.

.PARAMETER StartupTimeoutSeconds
    Seconds to wait for runtime.json + /health during start/drill.

.EXAMPLE
    powershell -NoProfile -File scripts\devhub-daemon.ps1 -Action status

.EXAMPLE
    powershell -NoProfile -File scripts\devhub-daemon.ps1 -Action drill
#>
[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'status', 'supervise', 'drill')]
    [string]$Action = 'status',

    [string]$Exe = '',

    [string]$DataDir = '',

    [int]$Port = 3091,

    [int]$StartupTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$script:ExePath = if ([string]::IsNullOrWhiteSpace($Exe)) {
    Join-Path $script:RepoRoot 'artifacts\publish\devhubd.exe'
} else {
    [IO.Path]::GetFullPath($Exe)
}
$script:DataDirExplicit = -not [string]::IsNullOrWhiteSpace($DataDir)
$script:DataDir = if ($script:DataDirExplicit) {
    [IO.Path]::GetFullPath($DataDir)
} else {
    Join-Path $env:LOCALAPPDATA 'userdrive-devhub'
}
$script:StopFlag = Join-Path $script:DataDir 'daemon.supervise.stop'
$script:SupervisorFile = Join-Path $script:DataDir 'daemon.supervise.json'

function Write-Step {
    param([string]$Message)
    Write-Host ("[devhub-daemon] {0}" -f $Message)
}

function Write-Fail {
    param([string]$Message)
    Write-Host ("[devhub-daemon] ERROR: {0}" -f $Message) -ForegroundColor Red
}

function Get-Runtime {
    $path = Join-Path $script:DataDir 'runtime.json'
    if (-not (Test-Path -LiteralPath $path)) {
        return $null
    }
    try {
        return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Test-ProcessAlive {
    param([int]$ProcessId)
    if ($ProcessId -le 0) {
        return $false
    }
    return $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

function Test-Health {
    param([int]$HealthPort)
    try {
        $response = Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/health" -f $HealthPort) -TimeoutSec 3
        return ($response.status -eq 'ok')
    } catch {
        return $false
    }
}

function Get-LiveRuntime {
    $runtime = Get-Runtime
    if ($null -eq $runtime) {
        return $null
    }
    if (-not (Test-ProcessAlive -ProcessId ([int]$runtime.pid))) {
        return $null
    }
    $healthPort = if ($runtime.mcpPort) { [int]$runtime.mcpPort } else { $Port }
    if (-not (Test-Health -HealthPort $healthPort)) {
        return $null
    }
    return $runtime
}

function Wait-DaemonReady {
    param([int]$TimeoutSeconds)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $runtime = Get-LiveRuntime
        if ($null -ne $runtime) {
            return $runtime
        }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Start-DaemonProcess {
    if (-not (Test-Path -LiteralPath $script:ExePath)) {
        throw ("devhubd.exe not found: {0}. Run: dotnet publish src/DevHubd/DevHubd.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish" -f $script:ExePath)
    }

    $logDir = Join-Path $script:DataDir 'logs'
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $stdout = Join-Path $logDir ("devhubd-{0}.out.log" -f $stamp)
    $stderr = Join-Path $logDir ("devhubd-{0}.err.log" -f $stamp)

    $arguments = @()
    if ($script:DataDirExplicit) {
        $arguments += '--data-dir'
        $arguments += ('"{0}"' -f $script:DataDir)
    }
    if ($Port -ne 3091) {
        $arguments += '--port'
        $arguments += $Port
    }

    $startParameters = @{
        FilePath               = $script:ExePath
        PassThru               = $true
        WindowStyle            = 'Hidden'
        RedirectStandardOutput = $stdout
        RedirectStandardError  = $stderr
    }
    if ($arguments.Count -gt 0) {
        $startParameters.ArgumentList = $arguments
    }

    # 注意：Start-Process 启动的子进程会继承当前终端的 Windows 作业对象（Job Object）。
    # 如果本脚本运行在会被回收的宿主里（IDE 集成终端 / CI 临时作业 / Agent shell），
    # 终端一关就会把 devhubd 连带杀掉（队列项表现为 LEASE_EXPIRED，日志无正常关闭记录）。
    # 需要长期驻留时，请先用 Invoke-CimMethod -ClassName Win32_Process -MethodName Create
    # 起一个脱离作业对象的 wrapper，再由 wrapper 调用本函数；详见 README §3.4。
    $process = Start-Process @startParameters

    return [pscustomobject]@{ Process = $process; StdOut = $stdout; StdErr = $stderr }
}

function Start-DaemonAndWait {
    $existing = Get-LiveRuntime
    if ($null -ne $existing) {
        return $existing
    }

    $launched = Start-DaemonProcess
    $runtime = Wait-DaemonReady -TimeoutSeconds $StartupTimeoutSeconds
    if ($null -ne $runtime) {
        return $runtime
    }

    $exitNote = 'still running but not healthy'
    if ($launched.Process.HasExited) {
        $exitNote = ("exited with code {0}" -f $launched.Process.ExitCode)
    }
    $tail = ''
    if (Test-Path -LiteralPath $launched.StdErr) {
        $tail = (Get-Content -LiteralPath $launched.StdErr -Tail 8 -Encoding UTF8) -join ' | '
    }
    throw ("daemon failed to become ready ({0}). logs: {1}. stderr: {2}" -f $exitNote, $launched.StdOut, $tail)
}

function Stop-Daemon {
    # Signal a supervisor first so it does not race us by restarting the process.
    New-Item -ItemType Directory -Force -Path $script:DataDir | Out-Null
    Set-Content -LiteralPath $script:StopFlag -Value 'stop' -Encoding ASCII

    $runtime = Get-Runtime
    $pidToStop = 0
    if ($null -ne $runtime) {
        $pidToStop = [int]$runtime.pid
    }

    if ($pidToStop -gt 0 -and (Test-ProcessAlive -ProcessId $pidToStop)) {
        $healthPort = if ($runtime.mcpPort) { [int]$runtime.mcpPort } else { $Port }
        try {
            Invoke-RestMethod -Method Post `
                -Uri ("http://127.0.0.1:{0}/internal/shutdown" -f $healthPort) `
                -Headers @{ Authorization = ('Bearer {0}' -f $runtime.token) } `
                -TimeoutSec 5 | Out-Null
        } catch {
            Write-Step 'graceful shutdown request failed; falling back to taskkill'
        }

        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Date) -lt $deadline -and (Test-ProcessAlive -ProcessId $pidToStop)) {
            Start-Sleep -Milliseconds 200
        }

        if (Test-ProcessAlive -ProcessId $pidToStop) {
            Write-Step ("graceful stop timed out; killing process tree {0}" -f $pidToStop)
            & taskkill.exe /PID $pidToStop /T /F | Out-Null
            Start-Sleep -Seconds 1
        }
    }

    # Wait for the host to remove runtime.json (ApplicationStopping handler).
    $runtimePath = Join-Path $script:DataDir 'runtime.json'
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and (Test-Path -LiteralPath $runtimePath)) {
        Start-Sleep -Milliseconds 200
    }
    if (Test-Path -LiteralPath $runtimePath) {
        # Stale discovery file from an earlier crash: safe to remove once no process holds it.
        Remove-Item -LiteralPath $runtimePath -Force -ErrorAction SilentlyContinue
    }

    Remove-Item -LiteralPath $script:StopFlag -Force -ErrorAction SilentlyContinue
    return $pidToStop
}

function Get-Supervisor {
    if (-not (Test-Path -LiteralPath $script:SupervisorFile)) {
        return $null
    }
    try {
        $state = Get-Content -LiteralPath $script:SupervisorFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if (Test-ProcessAlive -ProcessId ([int]$state.pid)) {
            return $state
        }
    } catch {
        return $null
    }
    return $null
}

function Invoke-Status {
    $runtime = Get-Runtime
    if ($null -eq $runtime) {
        Write-Step 'state=stopped runtime.json=absent'
        return 3
    }

    $alive = Test-ProcessAlive -ProcessId ([int]$runtime.pid)
    if (-not $alive) {
        Write-Step ("state=stopped runtime.json=stale pid={0}" -f $runtime.pid)
        return 3
    }

    $healthPort = if ($runtime.mcpPort) { [int]$runtime.mcpPort } else { $Port }
    $healthy = Test-Health -HealthPort $healthPort
    $supervisor = Get-Supervisor

    Write-Step ("state={0} pid={1} mcpPort={2} version={3} startedAt={4} deliveryMode={5}" -f `
        $(if ($healthy) { 'running' } else { 'unhealthy' }),
        $runtime.pid,
        $runtime.mcpPort,
        $runtime.version,
        $runtime.startedAt,
        $runtime.deliveryMode)
    Write-Step ("health={0} processAlive={1} supervisor={2}" -f `
        $(if ($healthy) { 'ok' } else { 'failed' }), $alive, $(if ($null -ne $supervisor) { $supervisor.pid } else { 'none' }))

    if ($healthy) { return 0 }
    return 4
}

function Invoke-Supervise {
    New-Item -ItemType Directory -Force -Path $script:DataDir | Out-Null
    Remove-Item -LiteralPath $script:StopFlag -Force -ErrorAction SilentlyContinue

    $state = [pscustomobject]@{ pid = $PID; startedAt = (Get-Date).ToString('O'); exe = $script:ExePath; dataDir = $script:DataDir }
    $state | ConvertTo-Json -Compress | Set-Content -LiteralPath $script:SupervisorFile -Encoding UTF8

    Write-Step ("supervise started pid={0} dataDir={1}" -f $PID, $script:DataDir)

    try {
        while (-not (Test-Path -LiteralPath $script:StopFlag)) {
            $runtime = Get-LiveRuntime
            if ($null -eq $runtime) {
                try {
                    $started = Start-DaemonAndWait
                    Write-Step ("daemon up pid={0} mcpPort={1}" -f $started.pid, $started.mcpPort)
                } catch {
                    Write-Step ("restart failed: {0}; retrying in 5s" -f $_.Exception.Message)
                    Start-Sleep -Seconds 5
                    continue
                }
            }
            Start-Sleep -Seconds 1
        }
    } finally {
        Remove-Item -LiteralPath $script:SupervisorFile -Force -ErrorAction SilentlyContinue
        Write-Step 'supervise stopped'
    }

    return 0
}

function Invoke-Drill {
    if ($null -ne (Get-LiveRuntime)) {
        throw 'drill requires a stopped daemon; run -Action stop first (drill kills the daemon process tree).'
    }

    Write-Step 'drill: starting supervisor'
    $supervisorArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath),
        '-Action', 'supervise',
        '-Port', $Port
    )
    if ($script:DataDirExplicit) {
        $supervisorArgs += @('-DataDir', ('"{0}"' -f $script:DataDir))
    }

    $supervisor = Start-Process -FilePath 'powershell.exe' -ArgumentList $supervisorArgs -PassThru -WindowStyle Hidden
    try {
        $first = Wait-DaemonReady -TimeoutSeconds $StartupTimeoutSeconds
        if ($null -eq $first) {
            throw 'drill: daemon did not become ready under the supervisor.'
        }
        $firstPid = [int]$first.pid
        Write-Step ("drill: daemon ready pid={0}; killing process tree" -f $firstPid)

        & taskkill.exe /PID $firstPid /T /F | Out-Null
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $deadline -and (Test-ProcessAlive -ProcessId $firstPid)) {
            Start-Sleep -Milliseconds 200
        }
        if (Test-ProcessAlive -ProcessId $firstPid) {
            throw ("drill: process {0} still alive after taskkill." -f $firstPid)
        }

        Write-Step 'drill: waiting for supervisor to restart the daemon'
        $second = $null
        $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            $runtime = Get-LiveRuntime
            if ($null -ne $runtime -and [int]$runtime.pid -ne $firstPid) {
                $second = $runtime
                break
            }
            Start-Sleep -Milliseconds 300
        }
        if ($null -eq $second) {
            throw 'drill: supervisor did not restart the daemon.'
        }
        Write-Step ("drill: restarted pid={0}" -f $second.pid)

        $smoke = Join-Path $script:RepoRoot 'scripts\smoke-mcp.mjs'
        $mcpUrl = ("http://127.0.0.1:{0}/mcp" -f $second.mcpPort)
        Write-Step ("drill: MCP smoke against {0}" -f $mcpUrl)
        & node $smoke $mcpUrl
        if ($LASTEXITCODE -ne 0) {
            throw ("drill: smoke-mcp failed with exit code {0}." -f $LASTEXITCODE)
        }

        Write-Step 'drill: PASS'
        return 0
    } finally {
        Set-Content -LiteralPath $script:StopFlag -Value 'stop' -Encoding ASCII
        $runtime = Get-Runtime
        if ($null -ne $runtime -and (Test-ProcessAlive -ProcessId ([int]$runtime.pid))) {
            try {
                Invoke-RestMethod -Method Post `
                    -Uri ("http://127.0.0.1:{0}/internal/shutdown" -f ([int]$runtime.mcpPort)) `
                    -Headers @{ Authorization = ('Bearer {0}' -f $runtime.token) } `
                    -TimeoutSec 5 | Out-Null
            } catch {
                Write-Step 'drill cleanup: graceful shutdown failed'
            }
        }
        Start-Sleep -Seconds 1
        if (-not $supervisor.HasExited) {
            & taskkill.exe /PID $supervisor.Id /T /F | Out-Null
        }
        Remove-Item -LiteralPath $script:StopFlag -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $script:SupervisorFile -Force -ErrorAction SilentlyContinue
    }
}

try {
    switch ($Action) {
        'start' {
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $runtime = Start-DaemonAndWait
            $watch.Stop()
            Write-Step ("started pid={0} mcpPort={1} startupMs={2} url={3}" -f $runtime.pid, $runtime.mcpPort, $watch.ElapsedMilliseconds, $runtime.mcpUrl)
            exit 0
        }
        'stop' {
            $stoppedPid = Stop-Daemon
            if ($stoppedPid -gt 0 -and (Test-ProcessAlive -ProcessId $stoppedPid)) {
                Write-Fail ("process {0} is still alive." -f $stoppedPid)
                exit 1
            }
            Write-Step ("stopped pid={0} runtime.json=removed" -f $stoppedPid)
            exit 0
        }
        'status' {
            exit (Invoke-Status)
        }
        'supervise' {
            exit (Invoke-Supervise)
        }
        'drill' {
            exit (Invoke-Drill)
        }
    }
} catch {
    Write-Fail $_.Exception.Message
    exit 1
}
