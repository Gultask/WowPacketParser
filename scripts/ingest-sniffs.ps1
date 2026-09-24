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
    restarted without redoing work. An archive is listed before it is opened: 7-Zip's listing
    carries every member's CRC-32 and size without unpacking anything, and an archive whose
    sniffs all match the sniff table's file_crc32 and file_size is passed over whole. Archives
    inside archives have no row of their own, so each one that finishes cleanly is noted in
    ingest_archive under the CRC its parent lists it with. Everything is logged with timestamps.

    Split archives are opened from their first volume (.001); 7-Zip reads the rest itself.

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
    #
    # Prefer -MapPolicy for a WotLK-and-below corpus: this drops whole sniffs, while the map
    # policy keeps the parts of a Cataclysm or Shadowlands capture that stand on ground a 3.3.5
    # server still has - Outland, Northrend, and every dungeon those expansions did not rebuild.
    [string] $MaxContentExpansion = '',

    # Drop packets by the map the client was standing on, before they are parsed. 'wotlk' keeps
    # only maps that exist in 3.3.5a Map.dbc, which is what makes a Cata+ corpus affordable: a
    # 4.4.1 capture measured 93.2% of its packets inside Firelands, a map no WotLK server has.
    [ValidateSet('', 'none', 'wotlk')]
    [string] $MapPolicy = '',

    # Extra map ids to drop on top of the policy, comma separated.
    [string] $MapDeny = '',

    # Parser worker threads; 0 means one per core. Leave it at 1. Collectors that read the
    # parser's Storage depend on the order packets were parsed in, and with more than one thread
    # creature auras, spawn areas and gameobject phases came out different on every run. The
    # database writes are the bottleneck anyway: five samples took 41 s on 1 thread, 40-42 s on 4.
    [int] $Threads = 1,

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
    Found = 0; Skipped = 0; Ingested = 0; Failed = 0; ArchivesOpened = 0; ArchivesSkipped = 0; Excluded = 0
}

$script:SniffExtensions = @('.pkt', '.bin')
$script:ArchiveExtensions = @('.7z', '.rar', '.zip', '.001')
# 'crc:size' of every sniff and nested archive already in the database, from Get-KnownCopies.
$script:KnownCopies = @{}

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

function Invoke-MySql {
    param([string] $Query)
    try {
        $env:MYSQL_PWD = $DbPassword
        $rows = & $MySql "-u$DbUser" -N -B -e $Query 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return @($rows)
    }
    finally {
        Remove-Item Env:\MYSQL_PWD -ErrorAction SilentlyContinue
    }
}

# Every sniff and nested archive already in the database, as 'crc:size', so an archive can be
# checked from its listing. Sniffs ingested before the parser recorded a CRC have none and
# simply cost an extraction, as they always did.
function Get-KnownCopies {
    if ($Force -or -not (Test-Path $MySql)) { return }

    [void] (Invoke-MySql ("CREATE TABLE IF NOT EXISTS ``$Database``.``ingest_archive`` (" +
        "``crc32`` CHAR(8) NOT NULL, ``size`` BIGINT UNSIGNED NOT NULL, ``name`` VARCHAR(512) NOT NULL, " +
        "``done_utc`` DATETIME NOT NULL, PRIMARY KEY (``crc32``, ``size``)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 " +
        "COMMENT='Archives found inside other archives whose sniffs were all ingested, keyed as their parent lists them; " +
        "written by ingest-sniffs.ps1 so a restart can pass over the parent unopened.';"))

    $sniffs = Invoke-MySql "SELECT LOWER(file_crc32), file_size FROM ``$Database``.``sniff`` WHERE file_crc32 IS NOT NULL;"
    $archives = Invoke-MySql "SELECT LOWER(crc32), size FROM ``$Database``.``ingest_archive``;"
    foreach ($row in @($sniffs) + @($archives)) {
        if (-not $row) { continue }
        $crc, $size = $row -split "`t"
        $script:KnownCopies["${crc}:$size"] = $true
    }
    Write-Log "$(@($sniffs).Count) sniffs and $(@($archives).Count) nested archives known by CRC"
}

# The archive's members from 7-Zip's listing, which reads headers only. $null when 7-Zip cannot
# list it; the archive is then opened as before.
function Get-ArchiveMembers {
    param([string] $Archive)

    $lines = & $SevenZip l -slt -- $Archive 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }

    $members = New-Object System.Collections.ArrayList
    $inList = $false
    $current = $null
    foreach ($line in $lines) {
        if ($line -eq '----------') { $inList = $true; continue }
        if (-not $inList) { continue }
        if ($line -match '^Path = (.*)$') {
            if ($current) { [void] $members.Add($current) }
            $current = @{ Path = $Matches[1]; Size = ''; Crc = ''; Folder = $false }
        }
        elseif ($current -and $line -match '^Size = (\d*)$') { $current.Size = $Matches[1] }
        elseif ($current -and $line -match '^CRC = ([0-9A-Fa-f]*)$') { $current.Crc = $Matches[1].ToLowerInvariant() }
        elseif ($current -and ($line -eq 'Folder = +' -or $line -match '^Attributes = D')) { $current.Folder = $true }
    }
    if ($current) { [void] $members.Add($current) }
    return , $members
}

# Reads back which of these members now have a sniff row, after the parser has had them.
function Update-KnownCopies {
    param($Members)
    $crcs = @($Members | Where-Object { $_.Crc -and $script:SniffExtensions -contains [IO.Path]::GetExtension($_.Path).ToLowerInvariant() } |
              ForEach-Object { "'$($_.Crc)'" })
    if ($crcs.Count -eq 0) { return }
    $rows = Invoke-MySql ("SELECT LOWER(file_crc32), file_size FROM ``$Database``.``sniff`` " +
                          "WHERE file_crc32 IN ($($crcs -join ','));")
    foreach ($row in @($rows)) {
        if (-not $row) { continue }
        $crc, $size = $row -split "`t"
        $script:KnownCopies["${crc}:$size"] = $true
    }
}

function Add-KnownArchive {
    param([string] $Identity, [string] $Name)
    if ($script:KnownCopies.ContainsKey($Identity)) { return }
    $crc, $size = $Identity -split ':'
    $slash = [string][char]92
    $escaped = $Name.Replace($slash, $slash + $slash).Replace("'", "''")
    [void] (Invoke-MySql ("INSERT IGNORE INTO ``$Database``.``ingest_archive`` " +
                          "VALUES ('$crc', $size, '$escaped', UTC_TIMESTAMP());"))
    $script:KnownCopies[$Identity] = $true
}

function Test-Excluded {
    param([string] $Name)
    if ($ExcludePattern -and $Name -match $ExcludePattern) { return $true }
    return $script:ExcludeNames.ContainsKey($Name) -or
           $script:ExcludeNames.ContainsKey([IO.Path]::GetFileNameWithoutExtension($Name))
}

# True when every sniff and nested archive in the listing is already in the database, so the
# archive holds nothing to do. Members that are neither - text files, SQL, screenshots - do not
# count, and an excluded sniff counts as done.
function Test-ArchiveDone {
    param($Members)

    $any = $false
    foreach ($m in $Members) {
        if ($m.Folder) { continue }
        $name = Split-Path -Leaf $m.Path
        $ext = [IO.Path]::GetExtension($name).ToLowerInvariant()
        if ($ext -eq '.gz') { return $false }
        if ($script:SniffExtensions -notcontains $ext -and $script:ArchiveExtensions -notcontains $ext) { continue }
        if ($script:SniffExtensions -contains $ext -and (Test-Excluded $name)) { continue }
        if (-not $m.Crc -or -not $script:KnownCopies.ContainsKey("$($m.Crc):$($m.Size)")) { return $false }
        $any = $true
    }
    return $any -or $Members.Count -gt 0
}

function Invoke-Parser {
    param([string[]] $Files)

    if ($Files.Count -eq 0) { return }

    if ($PSCmdlet.ShouldProcess("$($Files.Count) sniff(s)", 'parse into the ingest database')) {
        $arguments = @('--DumpFormat', '17', '--IngestDatabase', $Database)
        if ($MaxContentExpansion) {
            $arguments += @('--IngestMaxContentExpansion', $MaxContentExpansion)
        }
        if ($MapPolicy) {
            $arguments += @('--IngestMapPolicy', $MapPolicy)
        }
        if ($MapDeny) {
            $arguments += @('--IngestMapDeny', $MapDeny)
        }
        if ($Threads -gt 0) {
            $arguments += @('--Threads', $Threads)
        }
        $arguments += $Files
        # Streamed, not collected: a corrupt sniff once printed 845 MB of trace, and holding a
        # whole batch's output in memory before logging any of it stalled the run for good.
        & $Parser @arguments 2>&1 | ForEach-Object {
            $text = "$_"
            # The parser prints a progress fraction per packet; only keep the lines that matter.
            # 'tried to overwrite delegate' is long standing parser noise on Classic builds.
            if ($text -match 'Recorded as sniff|recorded|map gate|no loot -|Skipped -|WARNING|Could not|rror| failed: |no client locale|could not be read' -and
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

    # .001 is the first volume of a split archive; 7-Zip finds .002 onwards by itself.
    $wanted = '.pkt', '.bin', '.gz', '.7z', '.rar', '.zip', '.001'
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
    param([string] $Root, [hashtable] $Known, [System.Collections.ArrayList] $Pending, [int] $Depth = 0,
          # Inside an extracted archive: member path -> 'crc:size' from the parent's listing.
          [hashtable] $Listed = @{})

    if ($Depth -gt 4) {
        Write-Log "Archive nesting deeper than 4 at $Root - not descending further" 'WARN'
        return
    }

    foreach ($file in Get-Sniffs -Root $Root) {
        switch -Regex ($file.Extension) {
            '^\.(7z|rar|zip|001)$' {
                $identity = $null
                if ($Listed.Count -gt 0) {
                    $relative = $file.FullName.Substring($Root.TrimEnd([char]92).Length + 1)
                    $identity = $Listed[$relative.ToLowerInvariant()]
                }

                $members = if ($Force) { $null } else { Get-ArchiveMembers -Archive $file.FullName }
                if ($null -ne $members -and (Test-ArchiveDone -Members $members)) {
                    $script:Stats.ArchivesSkipped++
                    Write-Verbose "already ingested, not opened: $($file.Name)"
                    if ($identity -and -not $WhatIfPreference) { Add-KnownArchive -Identity $identity -Name $file.Name }
                    continue
                }
                $children = @{}
                foreach ($m in @($members)) {
                    if ($m -and $m.Crc) { $children[$m.Path.ToLowerInvariant()] = "$($m.Crc):$($m.Size)" }
                }

                # Some of these are zipped twice, so recurse rather than assuming one level.
                $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("wpp-ingest-" + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $temp -Force | Out-Null
                $failedBefore = $script:Stats.Failed
                try {
                    Write-Log "opening $($file.Name)"
                    if (Expand-Archive7z -Archive $file.FullName -Destination $temp) {
                        $script:Stats.ArchivesOpened++
                        Invoke-Path -Root $temp -Known $Known -Pending $Pending -Depth ($Depth + 1) -Listed $children
                        # Anything still queued refers to files inside this temp folder.
                        if ($Pending.Count -gt 0) {
                            Invoke-Parser -Files $Pending.ToArray()
                            $Pending.Clear()
                        }

                        # An archive inside an archive has no sniff row to be recognised by, so it
                        # is noted under the CRC its parent lists it with - once every sniff in it
                        # has a row. The parser exits 0 on a build it cannot read, so its exit code
                        # alone would mark an archive done that never went in.
                        if ($identity -and $null -ne $members -and -not $WhatIfPreference) {
                            Update-KnownCopies -Members $members
                        }
                        if ($identity -and $null -ne $members -and -not $WhatIfPreference -and
                            $script:Stats.Failed -eq $failedBefore -and (Test-ArchiveDone -Members $members)) {
                            Add-KnownArchive -Identity $identity -Name $file.Name
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
if ($MapPolicy) { Write-Log "map gate : $MapPolicy" }
if ($MapDeny) { Write-Log "map deny : $MapDeny" }
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
Get-KnownCopies
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
Write-Log ("found {0}, ingested {1}, skipped {2}, failed {3}, excluded {4}, archives opened {5}, archives already done {6}" -f `
    $script:Stats.Found, $script:Stats.Ingested, $script:Stats.Skipped, $script:Stats.Failed, `
    $script:Stats.Excluded, $script:Stats.ArchivesOpened, $script:Stats.ArchivesSkipped)
if ($script:Stats.Found -eq 0 -and $script:Stats.ArchivesSkipped -eq 0) {
    Write-Log "no sniff files were found in any of the given paths - nothing was ingested" 'ERROR'
}
Write-Log "log written to $LogFile"
