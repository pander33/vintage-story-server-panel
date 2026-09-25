# Публикация Windows-пакета из PowerShell (также запускается через powershell.exe из CMD).
# Результат: artifacts/win-x64 с тремя single-file .exe, install.cmd, VERSION и RID.
param(
	[ValidateSet('win-x64')]
	[string] $Rid = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$target = [System.IO.Path]::GetFullPath((Join-Path $artifacts $Rid))
$staging = [System.IO.Path]::GetFullPath((Join-Path $artifacts ('.publish-' + $Rid + '-' + [guid]::NewGuid().ToString('N'))))
$previous = [System.IO.Path]::GetFullPath((Join-Path $artifacts ('.previous-' + $Rid + '-' + [guid]::NewGuid().ToString('N'))))

# Перед рекурсивным перемещением/удалением проверяем абсолютные пути.
$prefix = $artifacts + [System.IO.Path]::DirectorySeparatorChar
foreach ($path in @($target, $staging, $previous)) {
	if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
		throw "Путь вне каталога артефактов: $path"
	}
}
if ((Test-Path -LiteralPath $artifacts) -and
	((Get-Item -LiteralPath $artifacts).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
	throw "Каталог артефактов является ссылкой: $artifacts"
}

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
New-Item -ItemType Directory -Path $staging | Out-Null
Push-Location $root
try {
	foreach ($project in @('Panel.Web', 'Panel.Agent', 'Panel.Cli')) {
		Write-Host "Публикация $project ($Rid)"
		& dotnet publish "src/$project/$project.csproj" `
			--configuration Release --runtime $Rid --self-contained true `
			-p:PublishSingleFile=true -p:PublishReadyToRun=true `
			-p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true `
			--output $staging
		if ($LASTEXITCODE -ne 0) { throw "Не удалось опубликовать $project (код $LASTEXITCODE)" }
	}

	Get-ChildItem -LiteralPath $staging -File |
		Where-Object { $_.Name -like '*.pdb' -or $_.Name -like 'appsettings*.json' -or
			$_.Name -like '*.staticwebassets.*.json' -or $_.Name -eq 'web.config' } |
		ForEach-Object { Remove-Item -LiteralPath $_.FullName }

	$props = [System.IO.File]::ReadAllText((Join-Path $root 'Directory.Build.props'))
	$match = [regex]::Match($props, '<PanelVersion>([^<]+)</PanelVersion>')
	if (-not $match.Success) { throw 'PanelVersion не найден в Directory.Build.props' }
	$version = $match.Groups[1].Value.Trim()
	$utf8 = [System.Text.UTF8Encoding]::new($false)
	[System.IO.File]::WriteAllText((Join-Path $staging 'VERSION'), $version + "`n", $utf8)
	[System.IO.File]::WriteAllText((Join-Path $staging 'RID'), $Rid + "`n", $utf8)

	# cmd.exe требует CRLF; нормализуем даже если исходник был скопирован с LF.
	$installer = [System.IO.File]::ReadAllText((Join-Path $root 'deploy/windows/install.cmd'))
	$installer = $installer -replace "`r?`n", "`r`n"
	[System.IO.File]::WriteAllText((Join-Path $staging 'install.cmd'), $installer, $utf8)

	$telemetry = [Environment]::GetEnvironmentVariable('VS_TELEMETRY_DIST')
	if ($telemetry) {
		$archives = @(Get-ChildItem -LiteralPath $telemetry -File -Filter 'vspaneltelemetry_*.zip')
		if ($archives.Count -eq 0) { throw "В $telemetry нет vspaneltelemetry_*.zip" }
		$destination = Join-Path $staging 'telemetry'
		New-Item -ItemType Directory -Path $destination | Out-Null
		foreach ($archive in $archives) { Copy-Item -LiteralPath $archive.FullName -Destination $destination }
	} else {
		Write-Warning 'Мод телеметрии не включён: задайте VS_TELEMETRY_DIST=<каталог с vspaneltelemetry_*.zip>'
	}

	foreach ($name in @('Panel.Web.exe', 'Panel.Agent.exe', 'Panel.Cli.exe', 'install.cmd', 'VERSION', 'RID')) {
		$file = Get-Item -LiteralPath (Join-Path $staging $name)
		if ($file.Length -eq 0) { throw "Пустой файл публикации: $name" }
	}

	$hadPrevious = Test-Path -LiteralPath $target
	if ($hadPrevious) {
		if ((Get-Item -LiteralPath $target).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
			throw "Каталог публикации является ссылкой: $target"
		}
		Move-Item -LiteralPath $target -Destination $previous
	}
	try {
		Move-Item -LiteralPath $staging -Destination $target
	} catch {
		if ($hadPrevious -and -not (Test-Path -LiteralPath $target)) {
			Move-Item -LiteralPath $previous -Destination $target
		}
		throw
	}
	if ($hadPrevious) {
		try { Remove-Item -LiteralPath $previous -Recurse -Force }
		catch { Write-Warning "Старый каталог остался в $previous`: $_" }
	}

	Write-Host "Готово: $target (версия $version). Установка: install.cmd; обновление: vs-panel update --from `"$target`""
} finally {
	Pop-Location
	if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
