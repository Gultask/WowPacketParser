<#
.SYNOPSIS
    Pours a folder of sniffs into the ingest database. Meant to be started and left alone.

.DESCRIPTION
    Walks the given paths for .pkt files and for archives (.7z, .rar, .zip, including archives
    inside archives), and runs WowPacketParser over each one with DumpFormat 17, which writes
    straight to MySQL and produces no files.

    Archives are extracted to a temporary folder one at a time and deleted straight after, so
    the whole of sniff-storage can be processed without needing 79 GB of free space.

    Files already in the sniff table are skipped by content hash, so the run can be stopped and
    restarted without redoing work. Everything is logged with timestamps.

.EXAMPLE
    .\ingest-sniffs.ps1
    Ingests the default dumps folder.

.EXAMPLE
    .\ingest-sniffs.ps1 -Path 'G:\sniff-storage' -LogFile 'G:\ingest.log'
    Ingests the shared archive storage overnight.

.EXAMPLE
    .\ingest-sniffs.ps1 -Path 'G:\sniff-storage','G:\Games\World of Warcraft\Sniff\dumps' -WhatIf
    Lists what would be ingested without running the parser.
#>
# PositionalBinding is off so that a stray argument cannot silently land in -Parser
# or -SevenZip instead of failing; only -Path may be given positionally.
[CmdletBinding(SupportsShouldProcess = $true, PositionalBinding = $false)]
param(
    [Parameter(Position = 0)]
    [string[]] $Path = @('G:\Games\World of Warcraft\Sniff\dumps'),

    # Resolved from the script's own folder in the body, not here: under
    # 'powershell -File', $PSScriptRoot is still empty while parameter defaults are
    # evaluated, but only when the script has a [CmdletBinding()] attribute.
    [string] $Parser,

    [string] $LogFile = (Join-Path (Get-Location) ("ingest-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))),

    [string] $SevenZip = 'C:\Program Files\7-Zip\7z.exe',

    [string] $MySql = 'C:\Program Files\MySQL\MySQL Server 8.4\bin\mysql.exe',

    [string] $Database = 'wpp_ingest',

    [string] $DbUser = 'root',

    [string] $DbPassword = 'root',

    # How many sniffs to hand the parser at once. Larger is slightly faster; smaller means a
    # crash on one bad sniff costs less.
    [int] $BatchSize = 10,

    # Skip sniffs showing a world newer than this without parsing them, e.g. 'WrathOfTheLichKing'
    # to keep everything up to and including Wrath Classic 3.4.x. Compares content rather than
    # build numbers, so Burning Crusade Classic is kept despite its very high build.
    [string] $MaxContentExpansion = '',

    # Parser worker threads; 0 means one per core. The work is not CPU parallel bound - two
    # threads finish within about a tenth of the time all twelve do - so capping this leaves the
    # machine usable for other things at almost no cost to throughput.
    [int] $Threads = 0,

    # Regex matched against the file name. Instance sniffs are the expensive ones - a handful of
    # raid logs carry roughly a third of the corpus's packets while holding under a tenth of its
    # creature spawns - so dropping them is the cheapest way to shorten an overworld-focused run.
    # Matching is on the name only, because the maps a sniff covers are not known until it is
    # parsed; sniff_map in the ingest database has that for anything already ingested.
    [string] $ExcludePattern,

    # File holding one sniff file name per line to skip; blank lines and lines starting with #
    # are ignored. Unlike -ExcludePattern this is exact rather than a guess from the name, so it
    # is the better tool once a corpus has been ingested once: scripts/ingest-queries.sql has the
    # query that writes the instance-dominated set straight out of sniff_map.
    [string] $ExcludeListFile,

    # Re-ingest sniffs that are already recorded instead of skipping them. Use after changing
    # what the parser extracts.
    [switch] $Force
)

# The parser and 7-Zip both write ordinary progress to stderr. Under 'Stop' PowerShell turns
# the first such line into a terminating error and the whole overnight run dies on file one,
# so failures are handled by checking exit codes explicitly instead.
$ErrorActionPreference = 'Continue'
$script:ExcludeNames = @{}
if ($ExcludeListFile) {
    if (-not (Test-Path -LiteralPath $ExcludeListFile)) {
        throw "Exclude list '$ExcludeListFile' does not exist."
    }
    foreach ($line in Get-Content -LiteralPath $ExcludeListFile) {
        $name = $line.Trim()
        if (-not $name -or $name.StartsWith('#')) { continue }
        $script:ExcludeNames[$name] = $true
        $script:ExcludeNames[[IO.Path]::GetFileNameWithoutExtension($name)] = $true
    }
}

$script:Stats = [ordered]@{
    Found = 0; Skipped = 0; Ingested = 0; Failed = 0; ArchivesOpened = 0; Excluded = 0
}

function Write-Log {
    param([string] $Message, [string] $Level = 'INFO')
    $line = "{0}  {1,-5} {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message
    Write-Host $line
    Add-Content -Path $LogFile -Value $line -Encoding utf8 -WhatIf:$false
}

function Get-IngestedHashes {
    if ($Force) { return @{} }
    if (-not (Test-Path $MySql)) {
        Write-Log "mysql.exe not found at '$MySql' - cannot skip already ingested sniffs, every file will be parsed" 'WARN'
        return @{}
    }

    $known = @{}
    try {
        $query = "SELECT file_hash FROM ``$Database``.``sniff``;"
        $env:MYSQL_PWD = $DbPassword
        $rows = & $MySql "-u$DbUser" -N -B -e $query
        foreach ($row in $rows) {
            $h = $row.Trim()
            if ($h) { $known[$h] = $true }
        }
        Write-Log "$($known.Count) sniffs already in the database"
    }
    catch {
        Write-Log "Could not read existing hashes ($($_.Exception.Message)) - every file will be parsed" 'WARN'
    }
    finally {
        Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
    }
    return $known
}

function Invoke-Parser {
    param([string[]] $Files)

    if ($Files.Count -eq 0) { return }

    if ($PSCmdlet.ShouldProcess("$($Files.Count) sniff(s)", 'parse into the ingest database')) {
        $arguments = @('--DumpFormat', '17')
        if ($MaxContentExpansion) {
            $arguments += @('--IngestMaxContentExpansion', $MaxContentExpansion)
        }
        if ($Threads -gt 0) {
            $arguments += @('--Threads', $Threads)
        }
        $arguments += $Files
        $output = & $Parser @arguments 2>&1
        foreach ($item in $output) {
            $text = "$item"
            # The parser prints a progress fraction per packet; only keep the lines that matter.
            # 'tried to overwrite delegate' is long standing parser noise on Classic builds.
            if ($text -match 'Recorded as sniff|spawns recorded|waypoints recorded|loot instances recorded|no loot -|Skipped -|WARNING|Could not|rror' -and
                $text -notmatch 'tried to overwrite delegate') {
                Write-Log $text.Trim()
            }
        }

        if ($LASTEXITCODE -ne 0) {
            Write-Log "Parser exited with code $LASTEXITCODE for batch of $($Files.Count)" 'WARN'
            $script:Stats.Failed += $Files.Count
        }
        else {
            $script:Stats.Ingested += $Files.Count
        }
    }
}

function Get-Sniffs {
    param([string] $Root)

    $wanted = '.pkt', '.bin', '.gz', '.7z', '.rar', '.zip'
    if (Test-Path -LiteralPath $Root -PathType Leaf) {
        return @(Get-Item -LiteralPath $Root)
    }
    $found = @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
               Where-Object { $wanted -contains $_.Extension.ToLowerInvariant() })
    $kept = $found
    if ($ExcludePattern) {
        $kept = @($kept | Where-Object { $_.Name -notmatch $ExcludePattern })
    }
    if ($script:ExcludeNames.Count -gt 0) {
        # Archives are matched on their own name and on the name they would carry with the
        # archive extension dropped, because the list is built from the .pkt names inside them.
        $kept = @($kept | Where-Object {
            -not ($script:ExcludeNames.ContainsKey($_.Name) -or
                  $script:ExcludeNames.ContainsKey([IO.Path]::GetFileNameWithoutExtension($_.Name)))
        })
    }
    $script:Stats.Excluded += ($found.Count - $kept.Count)
    return $kept
}

function Expand-Archive7z {
    param([string] $Archive, [string] $Destination)

    if (-not (Test-Path $SevenZip)) {
        Write-Log "7-Zip not found at '$SevenZip' - skipping archive $Archive" 'WARN'
        return $false
    }

    & $SevenZip x "-o$Destination" -y -bso0 -bsp0 -- $Archive | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Log "Could not extract $Archive (7z exit $LASTEXITCODE)" 'WARN'
        return $false
    }
    return $true
}

function Add-Pending {
    param([System.IO.FileInfo] $File, [hashtable] $Known, [System.Collections.ArrayList] $Pending)

    $script:Stats.Found++

    if (-not $Force) {
        $hash = (Get-FileHash -LiteralPath $File.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($Known.ContainsKey($hash)) {
            $script:Stats.Skipped++
            Write-Verbose "already ingested: $($File.Name)"
            return
        }
        $Known[$hash] = $true
    }

    [void] $Pending.Add($File.FullName)
    if ($Pending.Count -ge $BatchSize) {
        Invoke-Parser -Files $Pending.ToArray()
        $Pending.Clear()
    }
}

function Invoke-Path {
    param([string] $Root, [hashtable] $Known, [System.Collections.ArrayList] $Pending, [int] $Depth = 0)

    if ($Depth -gt 4) {
        Write-Log "Archive nesting deeper than 4 at $Root - not descending further" 'WARN'
        return
    }

    foreach ($file in Get-Sniffs -Root $Root) {
        switch -Regex ($file.Extension) {
            '^\.(7z|rar|zip)$' {
                # Some of these are zipped twice, so recurse rather than assuming one level.
                $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("wpp-ingest-" + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $temp -Force | Out-Null
                try {
                    Write-Log "opening $($file.Name)"
                    if (Expand-Archive7z -Archive $file.FullName -Destination $temp) {
                        $script:Stats.ArchivesOpened++
                        Invoke-Path -Root $temp -Known $Known -Pending $Pending -Depth ($Depth + 1)
                        # Anything still queued refers to files inside this temp folder.
                        if ($Pending.Count -gt 0) {
                            Invoke-Parser -Files $Pending.ToArray()
                            $Pending.Clear()
                        }
                    }
                }
                finally {
                    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
            default {
                Add-Pending -File $file -Known $Known -Pending $Pending
            }
        }
    }
}

# ---------------------------------------------------------------------------

if (-not $Parser) {
    $here = $PSScriptRoot
    if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }
    $Parser = Join-Path $here '..\WowPacketParser\bin\Release\WowPacketParser.exe'
}
$resolved = Resolve-Path -LiteralPath $Parser -ErrorAction SilentlyContinue
if ($resolved) { $Parser = $resolved.Path }

if (-not (Test-Path $Parser)) {
    throw "WowPacketParser.exe not found at '$Parser'. Build it in Release first, or pass -Parser."
}

Write-Log "=== ingest starting ==="
# Windows suspends on its idle timer even while this is working, because a background script
# generates no keyboard or mouse input - that is what ended the 2026-08-18 run mid archive.
# Ask for the same keep-awake a video player uses. It is scoped to this process and lapses
# when the script exits, so no power setting is changed and nothing needs undoing by hand.
try {
    if (-not ('WppIngest.SleepGuard' -as [type])) {
        Add-Type -Namespace WppIngest -Name SleepGuard -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@
    }
    # ES_CONTINUOUS | ES_SYSTEM_REQUIRED = 0x80000001. Written in decimal because PowerShell
    # reads the hex form as a negative Int32 and the cast to UInt32 then fails. Display may still sleep.
    if ([WppIngest.SleepGuard]::SetThreadExecutionState([uint32]2147483649) -eq 0) {
        Write-Log "could not ask Windows to stay awake - check the machine will not sleep mid run" 'WARN'
    }
    else {
        Write-Log "sleep suppressed for the duration of this run (display may still turn off)"
    }
}
catch {
    Write-Log "could not ask Windows to stay awake ($($_.Exception.Message))" 'WARN'
}

Write-Log "parser   : $Parser"
Write-Log "database : $Database"
if ($MaxContentExpansion) { Write-Log "cutoff   : $MaxContentExpansion and older" }
if ($Threads -gt 0) { Write-Log "threads  : $Threads" }
if ($ExcludePattern) { Write-Log "exclude  : /$ExcludePattern/" }
if ($ExcludeListFile) { Write-Log "exclude  : $($script:ExcludeNames.Count / 2) names from $ExcludeListFile" }
# 'powershell -File' does not evaluate PowerShell syntax in its arguments, so a comma
# separated list of paths arrives as a single string with commas inside it. Recover from
# that rather than spending the whole night finding nothing.
$expanded = @()
foreach ($root in $Path) {
    if ((Test-Path -LiteralPath $root) -or ($root -notmatch ',')) {
        $expanded += $root
        continue
    }
    $parts = @($root -split ',' | ForEach-Object { $_.Trim().Trim('"').Trim("'") } | Where-Object { $_ })
    $missing = @($parts | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($parts.Count -gt 1 -and $missing.Count -eq 0) {
        Write-Log "paths arrived as one comma joined string, reading them as $($parts.Count) separate paths" 'WARN'
        Write-Log "run the script from a PowerShell prompt rather than 'powershell -File' to avoid this" 'WARN'
        $expanded += $parts
    }
    else {
        $expanded += $root
    }
}
$Path = $expanded

Write-Log "paths    : $($Path -join '; ')"

$started = Get-Date
$known = Get-IngestedHashes
$pending = New-Object System.Collections.ArrayList

foreach ($root in $Path) {
    if (-not (Test-Path -LiteralPath $root)) {
        Write-Log "path not found, skipping: $root" 'WARN'
        continue
    }
    Write-Log "--- $root ---"
    Invoke-Path -Root $root -Known $known -Pending $pending
}

if ($pending.Count -gt 0) {
    Invoke-Parser -Files $pending.ToArray()
    $pending.Clear()
}

$elapsed = (Get-Date) - $started
Write-Log "=== finished in $($elapsed.ToString('hh\:mm\:ss')) ==="
Write-Log ("found {0}, ingested {1}, skipped {2}, failed {3}, excluded {4}, archives opened {5}" -f `
    $script:Stats.Found, $script:Stats.Ingested, $script:Stats.Skipped, $script:Stats.Failed, `
    $script:Stats.Excluded, $script:Stats.ArchivesOpened)
if ($script:Stats.Found -eq 0) {
    Write-Log "no sniff files were found in any of the given paths - nothing was ingested" 'ERROR'
}
Write-Log "log written to $LogFile"
