#requires -Version 7.0
<#
.SYNOPSIS
Prepare a release version and notes, or check a committed release before tagging.
.EXAMPLE
pwsh -File scripts/release.ps1 -Version 0.4.0
.EXAMPLE
pwsh -File scripts/release.ps1 -Version 0.4.0 -Mode Check
#>
[CmdletBinding(SupportsShouldProcess)]
param(
	[Parameter(Mandatory)]
	[string] $Version,
	[ValidateSet('Prepare', 'Check')]
	[string] $Mode = 'Prepare'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Invoke-Checked([string] $Tool, [string[]] $Arguments) {
	$global:LASTEXITCODE = 0
	& $Tool @Arguments
	if ($LASTEXITCODE -ne 0) { throw "$Tool failed (exit $LASTEXITCODE)." }
}

function Test-Release {
	Invoke-Checked dotnet @('restore', 'VintageStoryPanel.slnx')
	Invoke-Checked dotnet @('format', 'whitespace', 'VintageStoryPanel.slnx', '--no-restore', '--verify-no-changes')
	Invoke-Checked dotnet @('format', 'style', 'VintageStoryPanel.slnx', '--no-restore', '--verify-no-changes', '--severity', 'warn')
	Invoke-Checked dotnet @('build', 'VintageStoryPanel.slnx', '--no-restore', '--configuration', 'Release')
	Invoke-Checked dotnet @('test', 'VintageStoryPanel.slnx', '--no-build', '--configuration', 'Release')
	Invoke-Checked node @('--test', 'tests/frontend/api-token-session.test.js')
	Invoke-Checked git @('diff', '--check')
	Invoke-Checked git @('diff', '--cached', '--check')
}

# Only SemVer supported by the installer and CI; metadata (+...) is deliberately excluded.
$number = '(?:0|[1-9][0-9]*)'
$identifier = '(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)'
if ($Version -cnotmatch "^$number\.$number\.$number(?:-$identifier(?:\.$identifier)*)?\z") {
	throw 'Use a version such as 0.4.0 or 0.4.0-rc.1 (without v or build metadata).'
}

Push-Location $root
try {
	foreach ($tool in @('git', 'dotnet', 'node')) { Get-Command $tool -ErrorAction Stop | Out-Null }
	Invoke-Checked git @('rev-parse', '--show-toplevel')
	$propsPath = Join-Path $root 'Directory.Build.props'
	$props = [IO.File]::ReadAllText($propsPath)
	$pattern = '<PanelVersion>([^<]+)</PanelVersion>'
	$matches = [regex]::Matches($props, $pattern)
	if ($matches.Count -ne 1) { throw 'Expected exactly one PanelVersion in Directory.Build.props.' }
	$current = $matches[0].Groups[1].Value.Trim()
	$notesPath = Join-Path $root "docs/releases/$Version.md"
	$tag = "v$Version"
	$tags = @(& git tag --list $tag)
	if ($LASTEXITCODE -ne 0) { throw 'Cannot list Git tags.' }
	if ($tags.Count -gt 0) { throw "Tag $tag already exists. Choose a new version." }
	Write-Host "Release $tag ($Mode); current version: $current"

	if ($Mode -eq 'Prepare') {
		if (-not $PSCmdlet.ShouldProcess($root, "Prepare $tag and run release checks")) { return }
		$utf8 = [Text.UTF8Encoding]::new($false)
		if (-not (Test-Path -LiteralPath $notesPath)) {
			$template = @"
# Vintage Story Panel $Version

<!-- TODO: Write the release summary and replace all TODO markers before tagging. -->

## New

- TODO: Describe new features, or remove this section.

## Changed

- TODO: Describe changes, or remove this section.

## Fixed

- TODO: Describe fixes, or remove this section.

## Updating

TODO: Describe compatibility, migrations and any required actions for existing installations.

## Verification and known limitations

TODO: Record checks actually performed and outstanding integration checks.

## Downloads

- ``vs-panel-linux-x64.tar.gz`` — Linux x86-64.
- ``vs-panel-linux-arm64.tar.gz`` — Linux ARM64.
- ``vs-panel-win-x64.zip`` — Windows x86-64.
- ``get.sh``, ``get.ps1`` and ``SHA256SUMS`` — bootstrap scripts and checksums.

See the [README](https://github.com/pander33/vintage-story-server-panel/blob/$tag/README.md) for installation instructions.
"@
			[IO.Directory]::CreateDirectory((Split-Path $notesPath)) | Out-Null
			[IO.File]::WriteAllText($notesPath, ($template -replace '\r?\n', "`r`n") + "`r`n", $utf8)
		}
		if ($current -cne $Version) {
			$updated = [regex]::Replace($props, $pattern, "<PanelVersion>$Version</PanelVersion>")
			[IO.File]::WriteAllText($propsPath, ($updated -replace '\r?\n', "`r`n"), $utf8)
		}
		Write-Host "Version and notes prepared. Existing notes were preserved: $notesPath"
		Write-Host 'If a check fails, these edits remain available for correction and retry.'
	} else {
		if ($current -cne $Version) { throw "PanelVersion $current does not match $Version." }
		if (-not (Test-Path -LiteralPath $notesPath -PathType Leaf)) { throw "Missing release notes: $notesPath" }
		$notes = [IO.File]::ReadAllText($notesPath)
		if ([string]::IsNullOrWhiteSpace($notes) -or $notes -match '(?i)\bTODO\b') {
			throw 'Complete the release notes and remove TODO markers before tagging.'
		}
		$status = @(& git status --porcelain --untracked-files=all)
		if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git status.' }
		if ($status.Count -gt 0) { throw 'Commit the release changes first; the working tree must be clean.' }
		Invoke-Checked git @('ls-files', '--error-unmatch', "docs/releases/$Version.md")
		if (-not $PSCmdlet.ShouldProcess($root, "Check $tag before tagging")) { return }
	}

	Test-Release
	Write-Host "Checks passed. Release notes: docs/releases/$Version.md"
	if ($Mode -eq 'Prepare') {
		Write-Host "Next: complete notes, review git diff, commit changes, then run:"
		Write-Host "pwsh -File scripts/release.ps1 -Version $Version -Mode Check"
	} else {
		Write-Host 'After reviewing the commit, publish it with:'
		Write-Host "git tag -a $tag -m `"Vintage Story Panel $Version`""
		Write-Host 'git push origin HEAD'
		Write-Host "git push origin $tag"
		Write-Host 'Pushing the tag starts CI and publishes the GitHub Release automatically.'
	}
} finally {
	Pop-Location
}
