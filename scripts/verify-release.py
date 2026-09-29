# Проверка архивов релиза до загрузки: то, что потом распакуют get.sh, get.ps1 и
# vs-panel update (ReleaseSource), должно быть полным пакетом своей цели и версии.
#
#   python scripts/verify-release.py artifacts/release         архивы одной сборки, хотя бы один
#   python scripts/verify-release.py dist --all                все три цели релиза
#
# Версия берётся из Directory.Build.props. Каждый архив:
# - все записи внутри каталога vs-panel-<цель>, без абсолютных путей, «..» и ссылок;
# - три бинарника, VERSION с версией, RID с целью, мод телеметрии;
# - Linux: бинарники и install.sh исполняемые, install.sh с LF, владелец root;
# - Windows: install.cmd с CRLF — с LF cmd ломается.

import argparse
import posixpath
import re
import stat
import sys
import tarfile
import zipfile
from pathlib import Path

TARGETS = {
    'linux-x64': 'vs-panel-linux-x64.tar.gz',
    'linux-arm64': 'vs-panel-linux-arm64.tar.gz',
    'win-x64': 'vs-panel-win-x64.zip',
}

BINARIES = ('Panel.Web', 'Panel.Agent', 'Panel.Cli')


def panel_version(root):
    text = (root / 'Directory.Build.props').read_text(encoding='utf-8-sig')
    match = re.search(r'<PanelVersion>\s*([^<\s]+)\s*</PanelVersion>', text)
    if not match:
        sys.exit('PanelVersion is missing from Directory.Build.props')
    return match.group(1)


def safe_name(name, top):
    """Запись лежит внутри каталога пакета и не выходит из него."""
    if '\\' in name or name.startswith('/') or re.match(r'^[A-Za-z]:', name):
        return False
    parts = [part for part in name.split('/') if part not in ('', '.')]
    return bool(parts) and parts[0] == top and '..' not in parts


def check_contents(errors, archive, rid, version, files):
    """Общие правила; files — имя относительно каталога пакета → содержимое (bytes)."""
    windows = rid.startswith('win-')
    suffix = '.exe' if windows else ''
    for binary in BINARIES:
        if not files.get(binary + suffix):
            errors.append(f'{archive}: {binary}{suffix} is missing or empty')

    if files.get('VERSION', b'').decode('utf-8', 'replace').strip() != version:
        errors.append(f'{archive}: VERSION is not {version}')
    if files.get('RID', b'').decode('utf-8', 'replace').strip() != rid:
        errors.append(f'{archive}: RID is not {rid}')

    if not any(re.fullmatch(r'telemetry/vspaneltelemetry_[^/]+\.zip', name) and content for name, content in files.items()):
        errors.append(f'{archive}: telemetry/vspaneltelemetry_*.zip is missing')

    installer = 'install.cmd' if windows else 'install.sh'
    text = files.get(installer)
    if not text:
        errors.append(f'{archive}: {installer} is missing or empty')
    elif windows and re.search(rb'(?<!\r)\n', text):
        errors.append(f'{archive}: {installer} has LF line endings; cmd needs CRLF')
    elif not windows and b'\r' in text:
        errors.append(f'{archive}: {installer} has CR characters; sh needs LF')


def check_tar(errors, path, rid, version):
    top = f'vs-panel-{rid}'
    files = {}
    with tarfile.open(path, 'r:gz') as archive:
        for member in archive.getmembers():
            if not safe_name(member.name, top):
                errors.append(f'{path.name}: entry outside {top}/: {member.name}')
                continue
            if not (member.isfile() or member.isdir()):
                errors.append(f'{path.name}: {member.name} is not a regular file or directory')
                continue
            if member.uid != 0 or member.gid != 0:
                errors.append(f'{path.name}: {member.name} is not owned by root')
            if member.isfile():
                name = posixpath.relpath(posixpath.normpath(member.name), top)
                if name in files:
                    errors.append(f'{path.name}: duplicate file: {name}')
                files[name] = archive.extractfile(member).read()
                if name in (*BINARIES, 'install.sh') and not member.mode & 0o111:
                    errors.append(f'{path.name}: {name} is not executable')
    check_contents(errors, path.name, rid, version, files)


def check_zip(errors, path, rid, version):
    top = f'vs-panel-{rid}'
    files = {}
    with zipfile.ZipFile(path) as archive:
        for info in archive.infolist():
            if not safe_name(info.filename, top):
                errors.append(f'{path.name}: entry outside {top}/: {info.filename}')
                continue
            mode = info.external_attr >> 16
            if info.create_system == 3 and stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR):
                errors.append(f'{path.name}: {info.filename} is not a regular file or directory')
                continue
            if not info.is_dir():
                name = posixpath.relpath(posixpath.normpath(info.filename), top)
                if name in files:
                    errors.append(f'{path.name}: duplicate file: {name}')
                files[name] = archive.read(info)
    check_contents(errors, path.name, rid, version, files)


def main():
    parser = argparse.ArgumentParser(description='Verify panel release archives.')
    parser.add_argument('directory', type=Path)
    parser.add_argument('--all', action='store_true', help='require the archives of every target')
    arguments = parser.parse_args()

    version = panel_version(Path(__file__).resolve().parent.parent)
    errors = []
    present = {rid: arguments.directory / name for rid, name in TARGETS.items() if (arguments.directory / name).is_file()}

    unexpected = sorted(
        path.name for path in arguments.directory.glob('vs-panel-*')
        if path.is_file() and path.name not in TARGETS.values())
    errors += [f'{name}: not a release archive name' for name in unexpected]

    if arguments.all:
        errors += [f'{name} is missing' for rid, name in TARGETS.items() if rid not in present]
    elif not present:
        errors.append(f'no release archive in {arguments.directory}')

    for rid, path in present.items():
        try:
            (check_zip if path.suffix == '.zip' else check_tar)(errors, path, rid, version)
        except (tarfile.TarError, zipfile.BadZipFile, OSError, EOFError) as exception:
            errors.append(f'{path.name}: cannot be read: {exception}')

    if errors:
        print('\n'.join(errors), file=sys.stderr)
        return 1

    for rid, path in present.items():
        print(f'{path.name}: vs-panel {version} ({rid}), {path.stat().st_size / 1048576:.1f} MB')
    return 0


if __name__ == '__main__':
    sys.exit(main())
