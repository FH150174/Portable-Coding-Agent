[CmdletBinding()]
param(
    [string]$PackageRoot,
    [string]$ReportPath,
    [switch]$SkipSimulations
)

$ErrorActionPreference = 'Stop'
$expectedOpenCodeHash = 'da86eed515d91a7b2d7da9a8230a2bd095f68a89f0cf44eb6a9217bead81fffc'
$expectedLauncherHash = 'dcb118d93b538b27ba1071db295e6fca15dcf02fd8e7d9c993576e2108d08ee0'
$dummyKey = 'sk-VERIFY-00000000000000000000000000000000'
$results = [Collections.Generic.List[object]]::new()
if ([string]::IsNullOrWhiteSpace($PackageRoot)) {
    $PackageRoot = Join-Path $PSScriptRoot '..'
    $built = Join-Path $PackageRoot 'Release\Portable-Agent'
    if (Test-Path -LiteralPath (Join-Path $built 'Release-Manifest.json') -PathType Leaf) {
        $PackageRoot = $built
    }
}
$package = [IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')

function Record([string]$name, [string]$status, [string]$evidence) {
    $results.Add([pscustomobject]@{ name = $name; status = $status; evidence = $evidence })
    Write-Host ('{0,-10} {1,-20} {2}' -f $status, $name, $evidence)
}

function Invoke-Native([string]$file, [string]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $file
    $info.Arguments = $arguments
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw 'Could not start native launcher.' }
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'Native command exceeded 30 seconds.'
        }
        return [pscustomobject]@{
            code = $process.ExitCode
            output = $stdout.Result + [Environment]::NewLine + $stderr.Result
        }
    }
    finally { $process.Dispose() }
}

function New-MockServer([int]$statusCode) {
    if (-not ('PortableAgentVerificationMockServer' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

public sealed class PortableAgentVerificationMockServer : IDisposable {
    private readonly TcpListener listener;
    private readonly Thread worker;
    private readonly int statusCode;
    private volatile bool running = true;
    public int Port { get; private set; }

    public PortableAgentVerificationMockServer(int statusCode) {
        this.statusCode = statusCode;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        worker = new Thread(Serve);
        worker.IsBackground = true;
        worker.Start();
    }

    private void Serve() {
        while (running) {
            try {
                using (var client = listener.AcceptTcpClient()) {
                    client.ReceiveTimeout = 4000;
                    client.SendTimeout = 4000;
                    using (var stream = client.GetStream()) {
                        byte[] request = new byte[8192];
                        stream.Read(request, 0, request.Length);
                        string body = statusCode == 200
                            ? "{\"object\":\"list\",\"data\":[{\"id\":\"deepseek-flash\",\"object\":\"model\"}]}"
                            : "{\"error\":{\"message\":\"mock failure\"}}";
                        byte[] payload = Encoding.UTF8.GetBytes(body);
                        string reason = statusCode == 200 ? "OK" : statusCode == 401 ? "Unauthorized" : statusCode == 402 ? "Payment Required" : "Too Many Requests";
                        string headers = "HTTP/1.1 " + statusCode + " " + reason + "\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                        stream.Write(headerBytes, 0, headerBytes.Length);
                        stream.Write(payload, 0, payload.Length);
                    }
                }
            }
            catch (Exception) { if (!running) return; }
        }
    }

    public void Dispose() {
        running = false;
        listener.Stop();
        worker.Join(2000);
    }
}
"@
    }
    return [PortableAgentVerificationMockServer]::new($statusCode)
}

Write-Host "Package: $package"
Write-Host 'Only offline checks and a synthetic local API are used. A real key is never read.'
if (-not (Test-Path -LiteralPath $package -PathType Container)) {
    Record 'package-root' 'FAIL' 'Package directory is absent.'
}
else {
    $native = Join-Path $package 'Launcher\PortableAgent.exe'
    $openCode = Join-Path $package 'Agent\opencode.exe'
    $config = Join-Path $package 'Config\opencode.json'
    $files = @(
        'Launcher\PortableAgent.exe', 'Agent\opencode.exe', 'Config\opencode.json',
        'Config\approval-readonly.json', 'Config\approval-workspace.json',
        '启动 Agent.cmd', '启动终端界面.cmd', '退出 Agent.cmd', '查看诊断.cmd',
        '配置密钥.cmd', '设置审批档位.cmd', 'README.md', '验收手册.md', 'Tests\Verify-Package.cmd'
    )
    $missing = @($files | Where-Object { -not (Test-Path -LiteralPath (Join-Path $package $_) -PathType Leaf) })
    if ($missing.Count -eq 0) { Record 'file-layout' 'PASS' 'All package entry points and documents exist.' }
    else { Record 'file-layout' 'FAIL' ('Missing: ' + ($missing -join ', ')) }

    $dirs = @('Workspace', 'Logs', 'Data\config', 'Data\cache', 'Data\sessions', 'Data\temp', 'Data\run')
    $missing = @($dirs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $package $_) -PathType Container) })
    if ($missing.Count -eq 0) { Record 'data-layout' 'PASS' 'Workspace and portable data directories exist.' }
    else { Record 'data-layout' 'FAIL' ('Missing: ' + ($missing -join ', ')) }

    $trusted = $false
    if (Test-Path -LiteralPath $openCode -PathType Leaf) {
        $trusted = (Get-FileHash -LiteralPath $openCode -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expectedOpenCodeHash
        if ($trusted) { Record 'pinned-binary' 'PASS' 'OpenCode v1.18.32 EXE SHA-256 matches.' }
        else { Record 'pinned-binary' 'FAIL' 'OpenCode EXE hash differs from the pinned version.' }
    }
    else { Record 'pinned-binary' 'FAIL' 'Agent\opencode.exe is absent.' }

    if (Test-Path -LiteralPath $config -PathType Leaf) {
        try {
            $settings = Get-Content -LiteralPath $config -Encoding UTF8 -Raw | ConvertFrom-Json
            $valid = $settings.model -eq 'deepseek/deepseek-flash' -and
                $settings.provider.deepseek.options.apiKey -eq '{env:DEEPSEEK_API_KEY}' -and
                $settings.provider.deepseek.npm -eq '@ai-sdk/openai-compatible' -and
                $settings.provider.deepseek.options.baseURL -eq 'https://api.deepseek.com' -and
                $settings.autoupdate -eq $false -and $settings.share -eq 'disabled' -and
                $settings.permission.edit -eq 'ask' -and
                $settings.permission.external_directory -eq 'ask' -and
                $settings.permission.bash.'*' -eq 'ask' -and
                $settings.permission.bash.'git push*' -eq 'deny'
            if ($valid) { Record 'safe-config' 'PASS' 'Model, environment key, privacy and approval settings match.' }
            else { Record 'safe-config' 'FAIL' 'Model, privacy or approval settings differ.' }
        }
        catch { Record 'safe-config' 'FAIL' 'OpenCode configuration is invalid JSON.' }
    }
    else { Record 'safe-config' 'FAIL' 'Config\opencode.json is absent.' }

    $profileChecks = @(
        @{ name = 'approval-readonly.json'; hash = '28a70f06ea4ac04c1542c75c67f60f52c9368d38301183ef3a0363bddb1db8a1'; edit = 'deny'; bash = 'deny'; external = 'deny' },
        @{ name = 'approval-workspace.json'; hash = '3a32db90f754ffe764be0aea3fbdb44f78cd41bc74157a64965b68891aabd0ec'; edit = 'allow'; bash = 'ask'; external = 'ask' }
    )
    $profilesValid = $true
    foreach ($profile in $profileChecks) {
        $path = Join-Path $package ('Config\' + $profile.name)
        try {
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $profile.hash) { $profilesValid = $false; continue }
            $settings = Get-Content -LiteralPath $path -Encoding UTF8 -Raw | ConvertFrom-Json
            if ($settings.model -ne 'deepseek/deepseek-flash' -or
                $settings.provider.deepseek.options.apiKey -ne '{env:DEEPSEEK_API_KEY}' -or
                $settings.provider.deepseek.options.baseURL -ne 'https://api.deepseek.com' -or
                $settings.plugin.Count -ne 0 -or @($settings.mcp.PSObject.Properties).Count -ne 0 -or
                $settings.permission.edit -ne $profile.edit -or
                $settings.permission.external_directory -ne $profile.external -or
                $settings.permission.bash.'*' -ne $profile.bash -or
                $settings.permission.bash.'git push*' -ne 'deny') { $profilesValid = $false }
        }
        catch { $profilesValid = $false }
    }
    if ($profilesValid) { Record 'pinned-profiles' 'PASS' 'Both full approval configurations are pinned and preserve model/provider/privacy settings.' }
    else { Record 'pinned-profiles' 'FAIL' 'Approval configuration hash or settings differ.' }

    $manifestFile = Join-Path $package 'Release-Manifest.json'
    if (Test-Path -LiteralPath $manifestFile -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestFile -Encoding UTF8 -Raw | ConvertFrom-Json
            $bad = [Collections.Generic.List[string]]::new()
            if ($manifest.status -ne 'ready' -or $manifest.opencodeExeSha256 -ne $expectedOpenCodeHash) {
                $bad.Add('release metadata')
            }
            foreach ($entry in @($manifest.files)) {
                $relative = [string]$entry.path
                if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
                    $relative -match '^[A-Za-z]:' -or $relative.Replace('/', '\').Split('\') -contains '..' -or
                    $relative -ieq 'Config/deepseek.key') {
                    $bad.Add('unsafe path')
                    continue
                }
                $file = Join-Path $package $relative.Replace('/', '\')
                if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                    $bad.Add('missing: ' + $relative)
                    continue
                }
                if ((Get-Item -LiteralPath $file).Length -ne [long]$entry.bytes -or
                    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
                    $bad.Add('changed: ' + $relative)
                }
            }
            if ($bad.Count -eq 0) { Record 'manifest' 'PASS' "Verified $(@($manifest.files).Count) manifest entries." }
            else { Record 'manifest' 'FAIL' ($bad -join '; ') }
        }
        catch { Record 'manifest' 'FAIL' 'Release manifest cannot be checked.' }
    }
    else { Record 'manifest' 'UNVERIFIED' 'Release manifest absent; this may be source.' }

    if (Test-Path -LiteralPath (Join-Path $package 'Config\deepseek.key') -PathType Leaf) {
        Record 'key-handling' 'UNVERIFIED' 'User key exists; verifier did not read its content.'
    }
    else { Record 'key-handling' 'PASS' 'No real key bundled; configure on personal computer.' }

    $nativeTrusted = $false
    if (Test-Path -LiteralPath $native -PathType Leaf) {
        $nativeTrusted = (Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expectedLauncherHash
        if ($nativeTrusted) { Record 'launcher-hash' 'PASS' 'Native launcher matches independently pinned SHA-256.' }
        else { Record 'launcher-hash' 'FAIL' 'Native launcher differs from independently pinned SHA-256; it was not executed.' }
    }
    else { Record 'launcher-hash' 'FAIL' 'Native launcher is absent.' }

    if ($nativeTrusted) {
        try {
            $verification = Invoke-Native $native 'verify'
            if ($verification.code -eq 0 -and $verification.output -notmatch [regex]::Escape($dummyKey)) {
                Record 'native-verify' 'PASS' 'Native offline verifier exited 0.'
            }
            else { Record 'native-verify' 'FAIL' 'Native offline verifier reported failure.' }
        }
        catch { Record 'native-verify' 'FAIL' ('Native verifier could not run: ' + $_.Exception.GetType().Name) }
    }
    else { Record 'native-verify' 'UNVERIFIED' 'Untrusted or missing launcher was not executed.' }

    if ($nativeTrusted) {
        try {
            $assembly = [Reflection.Assembly]::LoadFile($native)
            $type = $assembly.GetType('PortableAgent', $true)
            $flags = [Reflection.BindingFlags]'NonPublic,Static'
            $modeField = $type.GetField('ApprovalMode', $flags)
            $keyField = $type.GetField('Key', $flags)
            $setEnvironment = $type.GetMethod('SetChildEnvironment', $flags)
            $configField = $type.GetField('ConfigText', $flags)

            $profiles = @(
                @{ level = 'readonly'; edit = 'deny'; bash = 'deny'; external = 'deny'; task = 'deny' },
                @{ level = 'manual'; edit = 'ask'; bash = 'ask'; external = 'ask'; task = $null },
                @{ level = 'workspace'; edit = 'allow'; bash = 'ask'; external = 'ask'; task = $null }
            )
            $validProfiles = $true
            foreach ($profile in $profiles) {
                $modeField.SetValue($null, $profile.level)
                $profileName = if ($profile.level -eq 'manual') { 'opencode.json' } else { 'approval-' + $profile.level + '.json' }
                $profilePath = Join-Path $package ('Config\' + $profileName)
                $configField.SetValue($null, [IO.File]::ReadAllText($profilePath, [Text.Encoding]::UTF8))
                $keyField.SetValue($null, $dummyKey)
                $child = [Diagnostics.ProcessStartInfo]::new()
                $child.UseShellExecute = $false
                $child.EnvironmentVariables['OPENCODE_PERMISSION'] = '{"edit":"allow","bash":{"*":"allow"}}'
                $setEnvironment.Invoke($null, @($child)) | Out-Null
                $overlayText = $child.EnvironmentVariables['OPENCODE_CONFIG_CONTENT']
                $overlay = $overlayText | ConvertFrom-Json
                $orderedBash = $overlayText.IndexOf('"*"') -lt $overlayText.IndexOf('"git push*"')
                if ($child.EnvironmentVariables['OPENCODE_PERMISSION'] -or
                    $overlay.permission.edit -ne $profile.edit -or
                    $overlay.permission.bash.'*' -ne $profile.bash -or
                    $overlay.permission.bash.'git push*' -ne 'deny' -or
                    $overlay.permission.external_directory -ne $profile.external -or
                    $overlay.model -ne 'deepseek/deepseek-flash' -or
                    $overlay.provider.deepseek.options.apiKey -ne '{env:DEEPSEEK_API_KEY}' -or
                    $overlay.provider.deepseek.options.baseURL -ne 'https://api.deepseek.com' -or
                    $overlay.permission.task -ne $profile.task -or
                    -not $orderedBash) { $validProfiles = $false }
            }
            $keyField.SetValue($null, $null)
            if ($validProfiles) { Record 'approval-profiles' 'PASS' 'Three tiers and inherited permission override checked in child environment.' }
            else { Record 'approval-profiles' 'FAIL' 'Approval tier or child environment differs from expected.' }
        }
        catch { Record 'approval-profiles' 'FAIL' ('Could not inspect native approval profiles: ' + $_.Exception.GetType().Name) }
    }
    else { Record 'approval-profiles' 'UNVERIFIED' 'Untrusted launcher was not inspected.' }

    if ($SkipSimulations) {
        Record 'mock-api' 'UNVERIFIED' 'Synthetic API cases explicitly skipped.'
    }
    elseif (-not $trusted -or -not $nativeTrusted -or
        -not (Test-Path -LiteralPath $config -PathType Leaf)) {
        Record 'mock-api' 'UNVERIFIED' 'Trusted components unavailable.'
    }
    else {
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        $fixture = Join-Path $tempRoot ('PortableAgentVerify-' + [guid]::NewGuid().ToString('N'))
        try {
            foreach ($dir in @('Launcher', 'Agent', 'Config', 'Workspace', 'Logs', 'Data\config',
                'Data\cache', 'Data\sessions', 'Data\temp', 'Data\home', 'Data\run')) {
                New-Item -ItemType Directory -Path (Join-Path $fixture $dir) -Force | Out-Null
            }
            Copy-Item -LiteralPath $native -Destination (Join-Path $fixture 'Launcher\PortableAgent.exe')
            Copy-Item -LiteralPath $config -Destination (Join-Path $fixture 'Config\opencode.json')
            foreach ($name in @('approval-readonly.json', 'approval-workspace.json')) {
                Copy-Item -LiteralPath (Join-Path $package ('Config\' + $name)) -Destination (Join-Path $fixture 'Config')
            }
            [IO.File]::WriteAllText((Join-Path $fixture 'Config\deepseek.key'), $dummyKey, [Text.UTF8Encoding]::new($false))
            $fixtureApp = Join-Path $fixture 'Launcher\PortableAgent.exe'
            $defaultApproval = Invoke-Native $fixtureApp 'approval --show'
            $setWorkspace = Invoke-Native $fixtureApp 'approval --level workspace'
            $workspaceApproval = Invoke-Native $fixtureApp 'approval --show'
            $setReadonly = Invoke-Native $fixtureApp 'approval --level readonly'
            $readonlyApproval = Invoke-Native $fixtureApp 'approval --show'
            $setManual = Invoke-Native $fixtureApp 'approval --level manual'
            $manualApproval = Invoke-Native $fixtureApp 'approval --show'
            $modePath = Join-Path $fixture 'Data\run\approval-mode.txt'
            if ($defaultApproval.code -eq 0 -and $defaultApproval.output -match '逐项审批' -and
                $setWorkspace.code -eq 0 -and $workspaceApproval.output -match '工作区编辑自动' -and
                $setReadonly.code -eq 0 -and $readonlyApproval.output -match '只读' -and
                $setManual.code -eq 0 -and $manualApproval.output -match '逐项审批' -and
                (Get-Content -LiteralPath $modePath -Raw).Trim() -eq 'manual') {
                Record 'approval-switch' 'PASS' 'Default and three persisted approval choices round trip.'
            }
            else { Record 'approval-switch' 'FAIL' 'Approval selection or persistence failed.' }

            $statePath = Join-Path $fixture 'Data\run\web-server.json'
            [IO.File]::WriteAllText($statePath, '{}', [Text.UTF8Encoding]::new($false))
            $blockedChange = Invoke-Native $fixtureApp 'approval --level workspace'
            Remove-Item -LiteralPath $statePath -Force
            [IO.File]::WriteAllText($modePath, 'invalid-mode', [Text.UTF8Encoding]::new($false))
            $badMode = Invoke-Native $fixtureApp 'approval --show'
            [IO.File]::WriteAllText($modePath, 'manual', [Text.UTF8Encoding]::new($false))
            if ($blockedChange.code -ne 0 -and $blockedChange.output -match 'PCA132' -and
                $badMode.code -ne 0 -and $badMode.output -match 'PCA135') {
                Record 'approval-guard' 'PASS' 'Running Web state blocks a switch; invalid mode fails closed.'
            }
            else { Record 'approval-guard' 'FAIL' 'Approval switch guard or invalid mode handling failed.' }

            $absentExe = Invoke-Native $fixtureApp 'preflight --api-base-url http://127.0.0.1:9'
            if ($absentExe.code -ne 0 -and $absentExe.output -match 'PCA102' -and
                $absentExe.output -notmatch [regex]::Escape($dummyKey)) {
                Record 'missing-exe' 'PASS' 'Missing EXE reports PCA102 and does not print dummy key.'
            }
            else { Record 'missing-exe' 'FAIL' 'Missing EXE was not diagnosed safely.' }

            Copy-Item -LiteralPath $openCode -Destination (Join-Path $fixture 'Agent\opencode.exe')
            $globalConfigDir = Join-Path $fixture 'Data\config\opencode'
            New-Item -ItemType Directory -Path $globalConfigDir -Force | Out-Null
            $globalConfigPath = Join-Path $globalConfigDir 'config.json'
            [IO.File]::WriteAllText($globalConfigPath,
                '{"agent":{"build":{"permission":{"edit":"allow","bash":"allow"}}}}',
                [Text.UTF8Encoding]::new($false))
            $blockedGlobal = Invoke-Native $fixtureApp 'preflight --api-base-url http://127.0.0.1:9'
            if ($blockedGlobal.code -ne 0 -and $blockedGlobal.output -match 'PCA122') {
                Record 'global-config-guard' 'PASS' 'OpenCode global config.json rejected before Key/API use.'
            }
            else { Record 'global-config-guard' 'FAIL' 'Unsafe OpenCode global config.json was not rejected.' }
            Remove-Item -LiteralPath $globalConfigPath -Force
            $agentConfigDir = Join-Path $globalConfigDir 'agents'
            New-Item -ItemType Directory -Path $agentConfigDir -Force | Out-Null
            $blockedAgent = Invoke-Native $fixtureApp 'preflight --api-base-url http://127.0.0.1:9'
            if ($blockedAgent.code -ne 0 -and $blockedAgent.output -match 'PCA122') {
                Record 'global-agent-guard' 'PASS' 'Custom global agents directory rejected before Key/API use.'
            }
            else { Record 'global-agent-guard' 'FAIL' 'Custom global agents directory was not rejected.' }
            Remove-Item -LiteralPath $agentConfigDir -Force
            $packageDir = Join-Path $globalConfigDir 'node_modules\example-package'
            $dependencyAgentDir = Join-Path $packageDir 'agents'
            New-Item -ItemType Directory -Path $dependencyAgentDir -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $packageDir 'config.json'), '{}', [Text.UTF8Encoding]::new($false))
            $dependencyAllowed = Invoke-Native $fixtureApp 'preflight --api-base-url http://127.0.0.1:9'
            if ($dependencyAllowed.code -ne 0 -and $dependencyAllowed.output -match 'PCA114') {
                Record 'dependency-config' 'PASS' 'Generated node_modules content is not mistaken for active global config.'
            }
            else { Record 'dependency-config' 'FAIL' 'Generated dependency content blocked startup or changed the expected preflight result.' }
            Remove-Item -LiteralPath (Join-Path $packageDir 'config.json') -Force
            Remove-Item -LiteralPath $dependencyAgentDir -Force
            Remove-Item -LiteralPath $packageDir -Force
            Remove-Item -LiteralPath (Join-Path $globalConfigDir 'node_modules') -Force



            foreach ($case in @(
                @{ status = 200; name = 'mock-200'; expected = '' },
                @{ status = 401; name = 'mock-401'; expected = 'PCA110' },
                @{ status = 402; name = 'mock-402'; expected = 'PCA111' },
                @{ status = 429; name = 'mock-429'; expected = 'PCA112' }
            )) {
                $server = New-MockServer $case.status
                try {
                    $reply = Invoke-Native $fixtureApp ("preflight --api-base-url http://127.0.0.1:" + $server.Port)
                    $exitOk = if ($case.expected -eq '') { $reply.code -eq 0 } else { $reply.code -ne 0 }
                    $messageOk = $case.expected -eq '' -or $reply.output.Contains($case.expected)
                    if ($exitOk -and $messageOk -and $reply.output -notmatch [regex]::Escape($dummyKey)) {
                        Record $case.name 'PASS' "Synthetic HTTP $($case.status) classified safely."
                    }
                    else { Record $case.name 'FAIL' "Synthetic HTTP $($case.status) result or redaction failed." }
                }
                finally { $server.Dispose() }
            }

            $socket = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
            $socket.Start()
            $closedPort = ([Net.IPEndPoint]$socket.LocalEndpoint).Port
            $socket.Stop()
            $offline = Invoke-Native $fixtureApp ("preflight --api-base-url http://127.0.0.1:" + $closedPort)
            if ($offline.code -ne 0 -and $offline.output -match 'PCA114' -and
                $offline.output -notmatch [regex]::Escape($dummyKey)) {
                Record 'mock-offline' 'PASS' 'Closed local port classified as network failure.'
            }
            else { Record 'mock-offline' 'FAIL' 'Closed local port did not yield safe network message.' }

            $logText = @(Get-ChildItem -LiteralPath (Join-Path $fixture 'Logs') -File -Recurse -ErrorAction SilentlyContinue |
                ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue }) -join [Environment]::NewLine
            if ($logText -notmatch [regex]::Escape($dummyKey)) {
                Record 'launcher-log-redaction' 'PASS' 'Dummy key absent from isolated launcher logs; OpenCode logs require live test.'
            }
            else { Record 'launcher-log-redaction' 'FAIL' 'Dummy key leaked into isolated launcher logs.' }
        }
        catch { Record 'mock-api' 'FAIL' ('Synthetic test failed: ' + $_.Exception.GetType().Name) }
        finally {
            $safe = [IO.Path]::GetFullPath($fixture).TrimEnd('\')
            if ($safe.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($safe).StartsWith('PortableAgentVerify-', [StringComparison]::Ordinal) -and
                (Test-Path -LiteralPath $safe -PathType Container)) {
                Remove-Item -LiteralPath $safe -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

$summary = [pscustomobject]@{
    pass = @($results | Where-Object status -eq 'PASS').Count
    fail = @($results | Where-Object status -eq 'FAIL').Count
    unverified = @($results | Where-Object status -eq 'UNVERIFIED').Count
}
Write-Host ("Checks: PASS={0}, FAIL={1}, UNVERIFIED={2}. F01-F10 still require manual acceptance." -f $summary.pass, $summary.fail, $summary.unverified)
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $report = [ordered]@{
        generatedUtc = [DateTime]::UtcNow.ToString('o')
        package = $package
        scope = 'Local static checks and synthetic API only; not two-computer validation.'
        summary = $summary
        results = @($results)
    }
    $target = [IO.Path]::GetFullPath($ReportPath)
    $parent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($target, (($report | ConvertTo-Json -Depth 6) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Host "Report: $target"
}
if ($summary.fail -gt 0) { exit 1 }
exit 0
