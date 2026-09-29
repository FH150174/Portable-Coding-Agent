[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$Download,
    [switch]$Zip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$version = '1.18.32'
$archiveName = 'opencode-windows-x64-baseline-v1.18.32.zip'
$officialUrl = 'https://github.com/anomalyco/opencode/releases/download/v1.18.32/opencode-windows-x64-baseline.zip'
$expectedArchiveBytes = 62101771
$expectedArchiveSha256 = 'cd852831bd094c2df2eb379eb98bed7a63db7f823a7caf277c732cdac33cbdb6'
$expectedExeSha256 = 'da86eed515d91a7b2d7da9a8230a2bd095f68a89f0cf44eb6a9217bead81fffc'
$expectedConfigSha256 = '57b2b36833676af90add9ca2875196177f4da38d336570825862374b12265472'
$expectedLauncherSha256 = '9a503822c3b817c2280588c9e76e3b71404b11bd359c4fd162df45bab0446a35'
$vendorDirectory = Join-Path $sourceRoot 'Vendor'
$archivePath = Join-Path $vendorDirectory $archiveName
$releaseBase = Join-Path $sourceRoot 'Release'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $releaseBase 'Portable-Agent'
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $sourceRoot $OutputDirectory
}
$outputPath = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$releaseBase = [IO.Path]::GetFullPath($releaseBase).TrimEnd('\')

if ($outputPath.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $sourceRoot.StartsWith($outputPath + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must not be the source directory or one of its parents.'
}
if ($outputPath.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and
    -not $outputPath.StartsWith($releaseBase + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'An output inside the source project must be under Release.'
}

$requiredFiles = @(
    (Join-Path $sourceRoot 'Config\opencode.json'),
    (Join-Path $sourceRoot 'Launcher\PortableAgent.exe'),
    (Join-Path $sourceRoot 'Launcher\PortableAgent.cs'),
    (Join-Path $sourceRoot 'Launcher\SessionRelocator.cs'),
    (Join-Path $sourceRoot 'Tools\Build-Launcher.cmd'),
    (Join-Path $sourceRoot '启动 Agent.cmd'),
    (Join-Path $sourceRoot '配置密钥.cmd')
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required source file is missing: $requiredFile"
    }
}
if (Test-Path -LiteralPath $outputPath) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
        throw 'OutputDirectory names an existing file.'
    }
    if ($null -ne (Get-ChildItem -LiteralPath $outputPath -Force | Select-Object -First 1)) {
        throw 'OutputDirectory is not empty. Choose a fresh directory; an existing key or workspace will never be overwritten.'
    }
}

function Test-PinnedArchive {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $length = (Get-Item -LiteralPath $Path).Length
    if ($length -eq 0) {
        return $false
    }
    if ($length -ne $expectedArchiveBytes) {
        throw "Pinned archive has the wrong byte count ($length; expected $expectedArchiveBytes)."
    }
    $sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sha256 -ne $expectedArchiveSha256) {
        throw 'Pinned archive SHA-256 mismatch. Do not run or package this archive.'
    }
    return $true
}

function Assert-NormalSourceFile {
    param([string]$Path)

    $parent = Get-Item -LiteralPath (Split-Path -Parent $Path) -Force
    $item = Get-Item -LiteralPath $Path -Force
    if ((($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -or
        $item.PSIsContainer -or
        (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Refusing linked or non-file source: $Path"
    }
}

function Assert-SafeSourceTree {
    param([string]$Root)

    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        $directoryItem = Get-Item -LiteralPath $directory -Force
        if (-not $directoryItem.PSIsContainer -or
            (($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) {
            throw "Refusing linked source directory: $directory"
        }
        foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing link or junction in source tree: $($child.FullName)"
            }
            if ($child.Name -ieq 'deepseek.key') {
                throw "Refusing key file in source tree: $($child.FullName)"
            }
            if ($child.PSIsContainer) {
                $pending.Push($child.FullName)
            }
        }
    }
}

$archiveReady = $false
try {
    $archiveReady = Test-PinnedArchive -Path $archivePath
}
catch {
    if (-not $Download) {
        throw
    }
    Write-Warning 'The existing archive is invalid. Downloading a fresh copy from the pinned official URL.'
}

$downloadTemp = $null
if ($Download -and -not $archiveReady) {
    if (-not (Test-Path -LiteralPath $vendorDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $vendorDirectory | Out-Null
    }
    $downloadTemp = Join-Path $vendorDirectory ('.opencode-download-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $oldProtocol = [Net.ServicePointManager]::SecurityProtocol
        try {
            [Net.ServicePointManager]::SecurityProtocol = $oldProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $officialUrl -OutFile $downloadTemp -UseBasicParsing -MaximumRedirection 5
        }
        finally {
            [Net.ServicePointManager]::SecurityProtocol = $oldProtocol
        }
        if (-not (Test-PinnedArchive -Path $downloadTemp)) {
            throw 'Official download did not create an archive.'
        }
        # Only replace the cached archive after both size and SHA-256 match.
        Move-Item -LiteralPath $downloadTemp -Destination $archivePath -Force
        $archiveReady = $true
    }
    finally {
        if (Test-Path -LiteralPath $downloadTemp -PathType Leaf) {
            Remove-Item -LiteralPath $downloadTemp -Force
        }
    }
}

$sourceExePath = Join-Path $sourceRoot 'Agent\opencode.exe'
$sourceExeReady = $false
if (-not $archiveReady -and (Test-Path -LiteralPath $sourceExePath -PathType Leaf)) {
    $sourceHash = (Get-FileHash -LiteralPath $sourceExePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($sourceHash -ne $expectedExeSha256) {
        throw 'Source Agent\opencode.exe SHA-256 mismatch. Do not package this executable.'
    }
    $sourceExeReady = $true
}
$binaryReady = $archiveReady -or $sourceExeReady

$outputParent = Split-Path -Parent $outputPath
if (-not (Test-Path -LiteralPath $outputParent -PathType Container)) {
    New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
}
$zipPath = $null
if ($Zip) {
    $zipName = (Split-Path -Leaf $outputPath) + '-v' + $version + '.zip'
    $zipPath = Join-Path $outputParent $zipName
    if (Test-Path -LiteralPath $zipPath) {
        throw 'The ZIP output already exists. Choose a fresh output directory or move the old ZIP first.'
    }
}
$stagePath = Join-Path $outputParent ('.portable-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagePath | Out-Null
$published = $false
$zipTemp = $null

try {
    foreach ($relativeDirectory in @(
        'Agent', 'Config', 'Tests', 'Launcher', 'Tools', 'Workspace', 'Logs',
        'Data\config', 'Data\cache', 'Data\sessions', 'Data\temp',
        'Data\home', 'Data\run'
    )) {
        New-Item -ItemType Directory -Path (Join-Path $stagePath $relativeDirectory) -Force | Out-Null
    }

    Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cmd' -File |
        ForEach-Object {
            Assert-NormalSourceFile -Path $_.FullName
            Copy-Item -LiteralPath $_.FullName -Destination $stagePath
        }
    foreach ($testName in @('Verify-Package.ps1', 'Verify-Package.cmd')) {
        $packageTestPath = Join-Path (Join-Path $sourceRoot 'Tests') $testName
        if (Test-Path -LiteralPath $packageTestPath -PathType Leaf) {
            Assert-NormalSourceFile -Path $packageTestPath
            Copy-Item -LiteralPath $packageTestPath -Destination (Join-Path $stagePath 'Tests')
        }
    }
    $sourceConfigPath = Join-Path $sourceRoot 'Config\opencode.json'
    Assert-NormalSourceFile -Path $sourceConfigPath
    if ((Get-FileHash -LiteralPath $sourceConfigPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedConfigSha256) {
        throw 'Pinned Config\opencode.json SHA-256 mismatch. Rebuild the native launcher after intentional config edits.'
    }
    Copy-Item -LiteralPath $sourceConfigPath -Destination (Join-Path $stagePath 'Config\opencode.json')

    $launcherExePath = Join-Path $sourceRoot 'Launcher\PortableAgent.exe'
    $launcherSourcePath = Join-Path $sourceRoot 'Launcher\PortableAgent.cs'
    $relocatorSourcePath = Join-Path $sourceRoot 'Launcher\SessionRelocator.cs'
    $buildHelperPath = Join-Path $sourceRoot 'Tools\Build-Launcher.cmd'
    foreach ($launcherFile in @($launcherExePath, $launcherSourcePath, $relocatorSourcePath, $buildHelperPath)) {
        Assert-NormalSourceFile -Path $launcherFile
    }
    Copy-Item -LiteralPath $launcherExePath -Destination (Join-Path $stagePath 'Launcher\PortableAgent.exe')
    Copy-Item -LiteralPath $launcherSourcePath -Destination (Join-Path $stagePath 'Launcher\PortableAgent.cs')
    Copy-Item -LiteralPath $relocatorSourcePath -Destination (Join-Path $stagePath 'Launcher\SessionRelocator.cs')
    Copy-Item -LiteralPath $buildHelperPath -Destination (Join-Path $stagePath 'Tools\Build-Launcher.cmd')
    $launcherSha256 = (Get-FileHash -LiteralPath (Join-Path $stagePath 'Launcher\PortableAgent.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($launcherSha256 -ne $expectedLauncherSha256) {
        throw 'Compiled launcher SHA-256 mismatch. Rebuild or update the pinned verified hash before assembly.'
    }

    $readmePath = Join-Path $sourceRoot 'README.md'
    if (Test-Path -LiteralPath $readmePath -PathType Leaf) {
        Assert-NormalSourceFile -Path $readmePath
        Copy-Item -LiteralPath $readmePath -Destination $stagePath
    }
    $acceptancePath = Join-Path $sourceRoot '验收手册.md'
    if (Test-Path -LiteralPath $acceptancePath -PathType Leaf) {
        Assert-NormalSourceFile -Path $acceptancePath
        Copy-Item -LiteralPath $acceptancePath -Destination $stagePath
    }
    $releaseDocs = Join-Path $stagePath 'Docs'
    New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
    foreach ($docName in @('OpenCode-LICENSE.txt', '技术记录.md')) {
        $docSource = Join-Path (Join-Path $sourceRoot 'Docs') $docName
        Assert-NormalSourceFile -Path $docSource
        Copy-Item -LiteralPath $docSource -Destination (Join-Path $releaseDocs $docName)
    }
    # Workspace is intentionally empty. Personal projects are copied to the USB after assembly.

    $binarySha256 = $null
    if ($archiveReady) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $executables = @($archive.Entries | Where-Object { $_.Name -ieq 'opencode.exe' -and $_.Length -gt 0 })
            if ($executables.Count -ne 1) {
                throw 'Pinned archive must contain exactly one nonempty opencode.exe.'
            }
            $entryStream = $executables[0].Open()
            $exePath = Join-Path $stagePath 'Agent\opencode.exe'
            $exeStream = [IO.File]::Create($exePath)
            try {
                $entryStream.CopyTo($exeStream)
                $exeStream.Flush($true)
            }
            finally {
                $exeStream.Dispose()
                $entryStream.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
        $binarySha256 = (Get-FileHash -LiteralPath (Join-Path $stagePath 'Agent\opencode.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    elseif ($sourceExeReady) {
        Copy-Item -LiteralPath $sourceExePath -Destination (Join-Path $stagePath 'Agent\opencode.exe')
        $binarySha256 = (Get-FileHash -LiteralPath (Join-Path $stagePath 'Agent\opencode.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($binaryReady -and $binarySha256 -ne $expectedExeSha256) {
        throw 'Extracted or copied opencode.exe SHA-256 mismatch.'
    }

    if (Test-Path -LiteralPath (Join-Path $stagePath 'Config\deepseek.key') -PathType Leaf) {
        throw 'Internal error: a key was copied into the release.'
    }
    $fileList = @(
        Get-ChildItem -LiteralPath $stagePath -File -Recurse -Force |
            Sort-Object FullName |
            ForEach-Object {
                [ordered]@{
                    path = $_.FullName.Substring($stagePath.Length + 1).Replace('\', '/')
                    bytes = $_.Length
                    sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                }
            }
    )
    $manifest = [ordered]@{
        product = 'Portable Coding Agent'
        buildTimeUtc = [DateTime]::UtcNow.ToString('o')
        status = if ($binaryReady) { 'ready' } else { 'incomplete: OpenCode binary absent' }
        opencodeVersion = $version
        officialArchiveUrl = $officialUrl
        officialArchiveBytes = $expectedArchiveBytes
        officialArchiveSha256 = $expectedArchiveSha256
        opencodeExeSha256 = $binarySha256
        launcherExeSha256 = $launcherSha256
        keyTarget = 'Config/deepseek.key (created by the user, never bundled)'
        files = $fileList
    }
    $json = $manifest | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText(
        (Join-Path $stagePath 'Release-Manifest.json'),
        $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false)
    )

    if ($Zip) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zipTemp = Join-Path $outputParent ('.portable-zip-' + [guid]::NewGuid().ToString('N') + '.tmp')
        # Create the ZIP from private staging, before anyone can configure a key
        # in the published release directory.
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $stagePath,
            $zipTemp,
            [IO.Compression.CompressionLevel]::Optimal,
            $false
        )
    }

    if (Test-Path -LiteralPath $outputPath -PathType Container) {
        Remove-Item -LiteralPath $outputPath
    }
    Move-Item -LiteralPath $stagePath -Destination $outputPath
    $published = $true

    if ($Zip) {
        Move-Item -LiteralPath $zipTemp -Destination $zipPath
        Write-Host "ZIP: $zipPath"
        Write-Host "ZIP SHA-256: $((Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    if ($binaryReady) {
        Write-Host "Ready release: $outputPath"
        Write-Host "OpenCode $version executable SHA-256: $binarySha256"
    }
    else {
        Write-Warning "Incomplete release: $outputPath"
        Write-Warning "Agent\opencode.exe is absent. On your own computer, run Assemble-Release.ps1 -Download into a fresh output directory."
    }
}
finally {
    if ($null -ne $zipTemp -and (Test-Path -LiteralPath $zipTemp -PathType Leaf)) {
        Remove-Item -LiteralPath $zipTemp -Force
    }
    if (-not $published -and (Test-Path -LiteralPath $stagePath -PathType Container)) {
        $safeParent = [IO.Path]::GetFullPath($outputParent).TrimEnd('\')
        $safeStage = [IO.Path]::GetFullPath($stagePath).TrimEnd('\')
        if ($safeStage.StartsWith($safeParent + '\', [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($safeStage).StartsWith('.portable-stage-', [StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $safeStage -Recurse -Force
        }
    }
}
