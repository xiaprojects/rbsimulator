# This file is part of RB.
#
# Copyright (C) 2026 XIAPROJECTS SRL
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, version 3.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program. If not, see <https://www.gnu.org/licenses/>.
#
# This source is part of the project RB: RB Simulator
# (github.com/xiaprojects/rbsimulator).
#
# Community edition will be free for all builders and personal use as defined by the licensing model
# Dual licensing for commercial agreement is available
# Please join Discord community
#
# smoke.ps1: runs a published rbsim-msfs.exe with the synthetic source and checks HTTP, WebSocket and serial output.
#
# Usage: ci/smoke.ps1 -Exe out/rbsim-msfs.exe [-Port 18090]
# Works in Windows PowerShell 5.1 and PowerShell 7.

param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$Port = 18090
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("rbsim-smoke-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$failures = New-Object System.Collections.Generic.List[string]

function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "ok   $what" } else { Write-Host "FAIL $what"; $failures.Add($what) }
}

function Test-NmeaLine([string]$line) {
    if ($line -notmatch '^\$([^*]+)\*([0-9A-F]{2})$') { return $false }
    $sum = 0
    foreach ($c in $Matches[1].ToCharArray()) { $sum = $sum -bxor [int]$c }
    return ('{0:X2}' -f $sum) -eq $Matches[2] -and $line.Length -le 250
}

# 1. --help and option errors
& $Exe --help | Out-Null
Check ($LASTEXITCODE -eq 0) '--help exits 0'
Set-Content -Path (Join-Path $work 'bad.json') -Value '{ "sourse": "msfs" }'
# The error goes to stderr, which Windows PowerShell 5.1 would turn into a terminating error.
$ErrorActionPreference = 'Continue'
& $Exe --config (Join-Path $work 'bad.json') 2>&1 | Out-Null
$badExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
Check ($badExit -eq 2) 'an unknown configuration key exits 2'

# 2. run the synthetic source with a file serial output from a configuration file
$capture = Join-Path $work 'capture.nmea'
$config = Join-Path $work 'smoke.json'
@{
    source = 'synthetic'; listen = '127.0.0.1'; port = $Port
    serial = @(@{ name = 'capture'; port = "file:$capture"; profile = 'rb07' })
} | ConvertTo-Json -Depth 4 | Set-Content -Path $config

$log = Join-Path $work 'run.log'
$proc = Start-Process -FilePath $Exe -ArgumentList @('--config', "`"$config`"") -PassThru -NoNewWindow `
    -RedirectStandardOutput $log -RedirectStandardError (Join-Path $work 'run.err')
try {
    $base = "http://127.0.0.1:$Port"
    $status = $null
    for ($i = 0; $i -lt 60 -and $null -eq $status; $i++) {
        Start-Sleep -Milliseconds 500
        try { $status = Invoke-RestMethod "$base/api/status" -TimeoutSec 2 } catch { }
    }
    Check ($null -ne $status) 'the web service answers /api/status'
    if ($null -ne $status) {
        Start-Sleep -Seconds 3
        $status = Invoke-RestMethod "$base/api/status"
        Check ($status.connected -eq $true) 'the synthetic source is connected'
        Check ($status.config -eq (Resolve-Path $config).Path) 'the configuration file is the one loaded'
        Check ($status.serial.Count -eq 1 -and $status.serial[0].open -eq $true) 'the serial output is open'
        Check ($status.rate_hz.state -ge 10) "the ownship update rate is at least 10 Hz ($($status.rate_hz.state))"

        $state = Invoke-RestMethod "$base/api/state"
        Check ($state.v -eq 1 -and $state.src -eq 'synthetic') 'GET /api/state is an RB-SIM-IF/1 message'
        foreach ($g in 'gps', 'ahrs', 'baro', 'airspeed') { Check ($null -ne $state.$g) "state group $g is valid" }
        $traffic = Invoke-RestMethod "$base/api/traffic"
        Check ($traffic.targets.Count -ge 1) 'GET /api/traffic has a target'

        $page = Invoke-WebRequest "$base/" -UseBasicParsing
        Check ($page.StatusCode -eq 200 -and $page.Content -match 'RB Simulator wrapper') 'the status page is served'

        $ws = New-Object System.Net.WebSockets.ClientWebSocket
        $ws.ConnectAsync([Uri]"ws://127.0.0.1:$Port/ws?rate=20", [Threading.CancellationToken]::None).Wait()
        $buf = New-Object byte[] 16384
        $seg = New-Object System.ArraySegment[byte] -ArgumentList @(, $buf)
        $res = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).Result
        $msg = [Text.Encoding]::UTF8.GetString($buf, 0, $res.Count) | ConvertFrom-Json
        Check ($msg.v -eq 1) 'the WebSocket sends RB-SIM-IF/1 messages'

        # a command over the WebSocket gets a control_result among the state messages
        $cmd = [Text.Encoding]::UTF8.GetBytes('{"control": {"com2_standby_hz": 124350000}}')
        $ws.SendAsync((New-Object System.ArraySegment[byte] -ArgumentList @(, $cmd)), 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $result = $null
        for ($i = 0; $i -lt 40 -and $null -eq $result; $i++) {
            $res = $ws.ReceiveAsync($seg, [Threading.CancellationToken]::None).Result
            $m = [Text.Encoding]::UTF8.GetString($buf, 0, $res.Count) | ConvertFrom-Json
            if ($null -ne $m.control_result) { $result = $m.control_result }
        }
        Check ($null -ne $result -and $result.ok -eq $true) 'a WebSocket control command is accepted'
        $ws.Dispose()

        # 2b. the control API: radios, autopilot and controls, verified by read-back
        $radio = Invoke-RestMethod "$base/api/radio"
        Check ($radio.com1.active_mhz -eq '118.100' -and $radio.xpdr.code -eq '7000') 'GET /api/radio reads the radio stack'
        $body = '{"com1_swap": true, "xpdr_code": "1200", "hdg_bug_mag_deg": 95, "ap_master": true, "flaps_pct": 30}'
        $post = Invoke-RestMethod "$base/api/control" -Method Post -ContentType 'application/json' -Body $body
        Check ($post.ok -eq $true -and $post.accepted.com1_active_hz -eq 121500000) 'POST /api/control accepts a command set'
        Start-Sleep -Seconds 3
        $ctl = (Invoke-RestMethod "$base/api/control").actuators
        foreach ($n in 'com1_active_hz', 'com1_standby_hz', 'xpdr_code', 'hdg_bug_mag_deg', 'ap_master', 'com2_standby_hz') {
            Check ($ctl.$n.command.status -eq 'confirmed') "$n is confirmed by read-back ($($ctl.$n.command.status))"
        }
        # the synthetic flaps snap to 25 % detents, so 30 % must be reported, not hidden
        Check ($ctl.flaps_pct.command.status -eq 'mismatch') "an ignored write is reported as mismatch ($($ctl.flaps_pct.command.status))"

        function Get-Code([scriptblock]$call) {
            try { & $call | Out-Null; return 200 } catch { return [int]$_.Exception.Response.StatusCode }
        }
        Check ((Get-Code { Invoke-WebRequest "$base/api/control" -Method Post -ContentType 'application/json' -Body '{"xpdr_code": "7800"}' -UseBasicParsing }) -eq 400) 'an invalid command is refused with 400'
        Check ((Get-Code { Invoke-WebRequest "$base/api/control" -Method Post -ContentType 'text/plain' -Body '{"gear_down": true}' -UseBasicParsing }) -eq 415) 'a non-JSON content type is refused with 415'
        Check ((Get-Code { Invoke-WebRequest "$base/api/control" -Method Post -ContentType 'application/json' -Headers @{ Origin = 'http://example.invalid' } -Body '{"gear_down": true}' -UseBasicParsing }) -eq 403) 'a command from a foreign web origin is refused with 403'
    }
}
finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Milliseconds 500
}

# 3. the device lines
$lines = @()
if (Test-Path $capture) { $lines = @(Get-Content $capture) }
Check ($lines.Count -gt 50) "the capture has device lines ($($lines.Count))"
$bad = @($lines | Where-Object { -not (Test-NmeaLine $_) })
Check ($bad.Count -eq 0) "every line has a valid checksum and length ($($bad.Count) bad)"
foreach ($id in 'GPRMC', 'GPGGA', 'PFLAA', 'PFLAU', 'RBATT', 'RBADC') {
    Check (@($lines | Where-Object { $_.StartsWith("`$$id,") }).Count -gt 0) "the capture contains `$$id"
}

if ($failures.Count -gt 0) {
    Write-Host "`n--- wrapper log"
    if (Test-Path $log) { Get-Content $log | Select-Object -Last 40 }
    Write-Host "`n$($failures.Count) smoke check(s) failed"
    exit 1
}
Remove-Item -Recurse -Force $work
Write-Host "`nall smoke checks passed"
