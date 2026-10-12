#requires -Version 7.0
# Prepares an isolated desktop profile for human first-playback acceptance.
# Default: prepare files only. -Start explicitly opens the normal desktop UI.
[CmdletBinding()]
param(
    [ValidateSet('Local', 'Server')]
    [string]$Scenario = 'Local',
    [switch]$Start,
    [string]$ApplicationPath
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) {
    throw 'This manual desktop preparation is supported on Windows only.'
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'orynivo-first-playback-' + [Guid]::NewGuid().ToString('N'))
$testRoot = [IO.Path]::GetFullPath($testRoot)
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
[IO.File]::WriteAllText((Join-Path $testRoot '.legacy-data-migrated'),
    'isolated manual acceptance profile')

$settings = @{
    LibraryPaths = @()
    OrynivoServers = @()
    PlexServers = @()
    OutputProfiles = @()
    CheckForUpdatesOnStartup = $false
    McpServerEnabled = $false
    MobileRemoteEnabled = $false
    LastMainView = 'Tracks'
    Language = 1
    Volume = 0.5
    CalculateMissingReplayGainDuringScan = $false
    ScheduledBackup = @{ Enabled = $false }
}
[IO.File]::WriteAllText((Join-Path $testRoot 'settings.json'),
    ($settings | ConvertTo-Json -Depth 6))

$musicFolder = $null
if ($Scenario -eq 'Local') {
    $musicFolder = Join-Path $testRoot 'test-music'
    [IO.Directory]::CreateDirectory($musicFolder) | Out-Null
    $wavePath = Join-Path $musicFolder 'First playback test.wav'
    $sampleRate = 44100
    $frameCount = $sampleRate * 3
    $dataLength = $frameCount * 4
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($wavePath))
    try {
        $writer.Write([Text.Encoding]::ASCII.GetBytes('RIFF'))
        $writer.Write([uint32](36 + $dataLength))
        $writer.Write([Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
        $writer.Write([uint32]16)
        $writer.Write([uint16]1)
        $writer.Write([uint16]2)
        $writer.Write([uint32]$sampleRate)
        $writer.Write([uint32]($sampleRate * 4))
        $writer.Write([uint16]4)
        $writer.Write([uint16]16)
        $writer.Write([Text.Encoding]::ASCII.GetBytes('data'))
        $writer.Write([uint32]$dataLength)
        for ($frame = 0; $frame -lt $frameCount; $frame++) {
            $seconds = $frame / [double]$sampleRate
            $fade = [Math]::Min(1, [Math]::Min($seconds / 0.1, (3 - $seconds) / 0.1))
            $sample = [int16][Math]::Round(
                32767 * 0.08 * $fade * [Math]::Sin(2 * [Math]::PI * 440 * $seconds))
            $writer.Write($sample)
            $writer.Write($sample)
        }
    }
    finally {
        $writer.Dispose()
    }
}

$testProcessId = $null
if ($Start) {
    if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
        $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
        $ApplicationPath = Join-Path $repositoryRoot (
            'Orynivo/bin/Release/net10.0-windows10.0.19041.0/Orynivo.exe')
    }
    $ApplicationPath = [IO.Path]::GetFullPath($ApplicationPath)
    if (-not [IO.File]::Exists($ApplicationPath)) {
        throw 'Build the Windows Release desktop app before starting this check.'
    }
    $processInfo = [Diagnostics.ProcessStartInfo]::new($ApplicationPath)
    $processInfo.UseShellExecute = $false
    $processInfo.WorkingDirectory = [IO.Path]::GetDirectoryName($ApplicationPath)
    $processInfo.Environment['ORYNIVO_DATA_DIR'] = $testRoot
    $processInfo.Environment.Remove('FANART_TV_API_KEY') | Out-Null
    $processInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    $testProcess = [Diagnostics.Process]::Start($processInfo)
    if ($null -eq $testProcess) {
        throw 'The isolated desktop process could not be started.'
    }
    $testProcessId = $testProcess.Id
    $testProcess.Dispose()
}

[pscustomobject]@{
    Scenario = $Scenario
    ProfileRoot = $testRoot
    MusicFolder = $musicFolder
    ProcessId = $testProcessId
}
