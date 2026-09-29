# Install or update the panel with one command: download the Windows package from GitHub
# Releases, verify its checksum and run the installer from it. In PowerShell:
#
#   irm https://raw.githubusercontent.com/ruslan120p/vintage-story-panel-net/master/deploy/windows/get.ps1 | iex
#
# With installer options (the same as install.cmd):
#
#   & ([scriptblock]::Create((irm <the same address>))) --access internet
#
# $env:VS_PANEL_VERSION = '0.3.0' selects a release instead of the latest.
#
# Works in Windows PowerShell 5.1 and PowerShell 7. ASCII only: irm | iex does not know the
# file encoding. Everything is inside one script block, so a truncated download runs nothing.

& {
	$ErrorActionPreference = 'Stop'
	# Windows PowerShell draws the progress bar for every received block and downloads many
	# times slower with it.
	$ProgressPreference = 'SilentlyContinue'
	$installerArguments = @($args)
	$repository = 'ruslan120p/vintage-story-panel-net'
	$self = "https://raw.githubusercontent.com/$repository/master/deploy/windows/get.ps1"
	$elevated = $env:VS_PANEL_GET_ELEVATED -eq '1'

	# Windows PowerShell 5.1 on older systems offers TLS 1.0 by default; GitHub requires 1.2.
	[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

	try {
		$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
		if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
			if ($elevated) {
				throw 'Administrator rights were not granted.'
			}

			# The elevated process does not inherit this environment: the release and the options
			# travel inside the command itself.
			Write-Host 'Administrator rights are required: confirm the prompt, installation continues in a new window.'
			$quoted = @($installerArguments | ForEach-Object { "'" + ([string] $_).Replace("'", "''") + "'" }) -join ' '
			$version = ([string] $env:VS_PANEL_VERSION).Replace("'", "''")
			$command = "[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12; " +
				"`$env:VS_PANEL_GET_ELEVATED = '1'; `$env:VS_PANEL_VERSION = '$version'; " +
				"& ([scriptblock]::Create((Invoke-RestMethod -UseBasicParsing '$self'))) $quoted"
			$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
			$shell = (Get-Process -Id $PID).Path
			Start-Process -FilePath $shell -Verb RunAs -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded)
			return
		}

		$architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
		# There is no win-arm64 package, and the installer refuses win-x64 on ARM64.
		if ($architecture -ne 'AMD64') {
			throw "Unsupported architecture: $architecture. The panel package is for 64-bit x86 Windows."
		}

		$rid = 'win-x64'
		$headers = @{ 'User-Agent' = 'vs-panel-get' }

		# One tag for both files: latest/download could serve them from different releases if a
		# new one is published in between.
		if ($env:VS_PANEL_VERSION) {
			$tag = ([string] $env:VS_PANEL_VERSION).Trim()
			if (-not $tag.StartsWith('v')) { $tag = 'v' + $tag }
		} else {
			$tag = [string] (Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri "https://api.github.com/repos/$repository/releases/latest").tag_name
		}

		if ($tag -notmatch '^v[0-9A-Za-z][0-9A-Za-z._-]*$') {
			throw "Invalid release: $tag"
		}

		$package = "vs-panel-$rid.zip"
		$base = "https://github.com/$repository/releases/download/$tag"

		# Only SYSTEM and administrators may write here: an ordinary process of the same user
		# could otherwise replace Panel.Cli.exe between the checksum and the elevated start.
		$work = Join-Path ([IO.Path]::GetTempPath()) ('vs-panel-' + [guid]::NewGuid().ToString('N'))
		$security = New-Object System.Security.AccessControl.DirectorySecurity
		$security.SetAccessRuleProtection($true, $false)
		foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
			$identity = New-Object System.Security.Principal.SecurityIdentifier $sid
			$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')
			$security.AddAccessRule($rule)
		}

		$directoryInfo = New-Object System.IO.DirectoryInfo $work
		if ($PSVersionTable.PSEdition -eq 'Core') {
			[System.IO.FileSystemAclExtensions]::Create($directoryInfo, $security) | Out-Null
		} else {
			$directoryInfo.Create($security)
		}

		try {
			Write-Host "Downloading vs-panel $($tag.Substring(1)) ($rid)"
			$sums = Join-Path $work 'SHA256SUMS'
			$archive = Join-Path $work $package
			Invoke-WebRequest -UseBasicParsing -Headers $headers -Uri "$base/SHA256SUMS" -OutFile $sums
			Invoke-WebRequest -UseBasicParsing -Headers $headers -Uri "$base/$package" -OutFile $archive

			$expected = $null
			foreach ($line in Get-Content -LiteralPath $sums) {
				$parts = $line.Trim() -split '\s+', 2
				if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $package) {
					$expected = $parts[0].ToLowerInvariant()
					break
				}
			}

			if (-not $expected) {
				throw "Release $tag has no checksum for $package"
			}

			$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
			if ($actual -ne $expected) {
				throw "Checksum mismatch for ${package}: the download is corrupt, try again"
			}

			Expand-Archive -LiteralPath $archive -DestinationPath $work
			$directory = Join-Path $work "vs-panel-$rid"
			$cli = Join-Path $directory 'Panel.Cli.exe'
			if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
				throw "Panel.Cli.exe is missing from $package"
			}
			if ((Get-Content -LiteralPath (Join-Path $directory 'VERSION') -Raw).Trim() -ne $tag.Substring(1)) {
				throw "Package version does not match $tag"
			}
			if ((Get-Content -LiteralPath (Join-Path $directory 'RID') -Raw).Trim() -ne $rid) {
				throw "Package target does not match $rid"
			}

			& $cli install --from $directory @installerArguments
			if ($LASTEXITCODE -ne 0) {
				throw "The installer finished with code $LASTEXITCODE."
			}
		} finally {
			Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
		}
	} catch {
		Write-Host "vs-panel: $($_.Exception.Message)" -ForegroundColor Red
		throw
	} finally {
		# A new window closes as soon as the script ends: let the reader see the result.
		if ($elevated) {
			Read-Host 'Press Enter to close this window' | Out-Null
		}
	}
} @args
