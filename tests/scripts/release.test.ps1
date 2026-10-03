#requires -Version 7.0
# Exercise release preparation in an isolated repository. Build tools are mocked here;
# the real solution and frontend checks run separately in CI.
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../../scripts/release.ps1'
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('vs-panel-release-test-' + [guid]::NewGuid().ToString('N'))
$testCount = 0
$global:releaseTestCalls = @()
$global:releaseTestFailBuild = $false

function dotnet {
	$global:releaseTestCalls += 'dotnet ' + ($args -join ' ')
	$global:LASTEXITCODE = if ($global:releaseTestFailBuild -and $args[0] -eq 'build') { 7 } else { 0 }
}

function node {
	$global:releaseTestCalls += 'node ' + ($args -join ' ')
	$global:LASTEXITCODE = 0
}

function Assert([bool] $Condition, [string] $Message) {
	if (-not $Condition) { throw $Message }
	$script:testCount++
}

function Assert-Fails([scriptblock] $Action, [string] $Pattern) {
	$message = $null
	try { & $Action } catch { $message = $_.Exception.Message }
	Assert ($message -and $message -match $Pattern) "Expected failure matching '$Pattern', got '$message'."
}

function Invoke-FixtureGit([string[]] $Arguments) {
	& git -C $sandbox @Arguments | Out-Null
	if ($LASTEXITCODE -ne 0) { throw 'Fixture git command failed.' }
}

try {
	[IO.Directory]::CreateDirectory((Join-Path $sandbox 'scripts')) | Out-Null
	Copy-Item -LiteralPath $source -Destination (Join-Path $sandbox 'scripts/release.ps1')
	$release = Join-Path $sandbox 'scripts/release.ps1'
	$props = Join-Path $sandbox 'Directory.Build.props'
	$notes = Join-Path $sandbox 'docs/releases/0.4.0.md'
	[IO.File]::WriteAllText($props, "<Project>`r`n`t<PanelVersion>0.3.0</PanelVersion>`r`n</Project>`r`n")
	[IO.File]::WriteAllText((Join-Path $sandbox '.gitattributes'), "* text=auto`n*.props text eol=crlf`n*.md text eol=crlf`n*.ps1 text eol=crlf`n")
	Invoke-FixtureGit @('init', '--quiet')
	Invoke-FixtureGit @('config', 'user.name', 'Release test')
	Invoke-FixtureGit @('config', 'user.email', 'release-test@example.invalid')
	Invoke-FixtureGit @('config', 'core.autocrlf', 'false')
	Invoke-FixtureGit @('add', '.')
	Invoke-FixtureGit @('commit', '--quiet', '-m', 'Fixture')

	foreach ($invalid in @('v0.4.0', '../0.4.0', '01.4.0', '0.4', '0.4.0+build', '0.4.0-01')) {
		Assert-Fails { & $release -Version $invalid } 'Use a version'
	}
	Assert-Fails { & $release -Version "0.4.0`n" } 'Use a version'
	& $release -Version '0.4.0' -WhatIf
	Assert (([IO.File]::ReadAllText($props) -match '0.3.0') -and -not (Test-Path $notes) -and $global:releaseTestCalls.Count -eq 0) 'WhatIf must not edit or execute build tools.'
	& $release -Version '0.4.0'
	Assert (([IO.File]::ReadAllText($props) -match '0.4.0') -and (Test-Path $notes)) 'Preparation did not create the version and notes.'
	Assert ($global:releaseTestCalls.Count -eq 5 -and $global:releaseTestCalls[-1] -like 'node *') 'Preparation did not invoke every build/frontend check.'
	Assert-Fails { & $release -Version '0.4.0' -Mode Check } 'TODO'
	[IO.File]::WriteAllText($notes, "# Vintage Story Panel 0.4.0`r`n`r`nReviewed release notes.`r`n")
	$before = [IO.File]::ReadAllText($notes)
	& $release -Version '0.4.0'
	Assert ([IO.File]::ReadAllText($notes) -ceq $before) 'Repeated preparation overwrote existing notes.'
	Assert-Fails { & $release -Version '0.4.0' -Mode Check } 'working tree must be clean'
	Assert-Fails { & $release -Version '0.5.0' -Mode Check } 'does not match'
	Invoke-FixtureGit @('add', '.')
	Invoke-FixtureGit @('commit', '--quiet', '-m', 'Release fixture')
	& $release -Version '0.4.0' -Mode Check
	Assert (@(& git -C $sandbox status --porcelain).Count -eq 0) 'Check mode changed the repository.'
	Invoke-FixtureGit @('tag', 'v0.4.0')
	Assert-Fails { & $release -Version '0.4.0' } 'already exists'
	$global:releaseTestFailBuild = $true
	$global:releaseTestCalls = @()
	Assert-Fails { & $release -Version '0.5.0' } 'exit 7'
	Assert ($global:releaseTestCalls.Count -eq 3) 'Failure must stop before tests and frontend checks.'
	Assert ([IO.File]::ReadAllText($props) -match '0.5.0') 'Failed checks must retain prepared edits.'
	$global:releaseTestFailBuild = $false
	& $release -Version '0.5.0-rc.1' -WhatIf
	Assert ([IO.File]::ReadAllText($props) -match '0.5.0</') 'Prerelease WhatIf changed the version.'
	& $release -Version '0.5.0-rc.1'
	Assert (Test-Path (Join-Path $sandbox 'docs/releases/0.5.0-rc.1.md')) 'Prerelease notes were not created.'
	Assert-Fails { & $release -Version '0.5.0-RC.1' -Mode Check } 'does not match'
	Write-Host "$testCount release script assertions passed."
} finally {
	$resolved = [IO.Path]::GetFullPath($sandbox)
	$tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
	if (-not $resolved.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
		[IO.Path]::GetFileName($resolved) -notlike 'vs-panel-release-test-*') { throw 'Unsafe fixture cleanup path.' }
	if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
