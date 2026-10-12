#!/usr/bin/env python3
"""V1.9 offline deployment-backup inspector. NEVER restores, overwrites or connects to a live DB.

Example: python tools/recovery_sandbox.py --data-dir /path/to/service/data --deployment-id dep-<32 hex>
Run this tool only on a local, trusted deployment data directory. Output never includes DB contents.
"""
import argparse, hashlib, json, re, shutil, sqlite3, tempfile
from contextlib import closing
from pathlib import Path
from urllib.parse import quote

MAX_BYTES = 128 * 1024 * 1024

def inspect(data_dir: Path, deployment_id: str) -> dict:
    if re.fullmatch(r'dep-[a-f0-9]{32}', deployment_id) is None:
        raise ValueError('invalid deployment id')
    root = data_dir.resolve(strict=True)
    deployments = root / 'deployments'
    journal = deployments / (deployment_id + '.json')
    record = json.loads(journal.read_text(encoding='utf-8'))
    if record.get('Id') != deployment_id:
        raise ValueError('journal ID mismatch')
    station = record.get('StationId', '')
    if not isinstance(station, str) or re.fullmatch(r'[A-Za-z0-9_-]{1,32}', station) is None:
        raise ValueError('invalid station id')
    before_flow = record.get('BeforeFlow') or {}
    before_recipe = record.get('BeforeRecipe') or {}
    expected = deployments / 'backups' / station / (deployment_id + '.db')
    result = {'deploymentId': deployment_id, 'stationId': station, 'readOnly': True,
              'writesProduction': False, 'requiresManualReview': True, 'backupValidated': False,
              'backupFlowId': None, 'backupRecipeId': None, 'checks': []}
    def check(name, passed, detail):
        result['checks'].append({'code': name, 'passed': bool(passed), 'message': detail})
    raw = record.get('BackupFile')
    # Restrict the persisted relative path to one exact expected location; no arbitrary files.
    valid_path = (isinstance(raw, str) and raw.replace('\\', '/') == f'backups/{station}/{deployment_id}.db'
                  and not expected.is_symlink() and not expected.parent.is_symlink()
                  and not (deployments / 'backups').is_symlink() and not deployments.is_symlink())
    check('backup-path', valid_path, 'controlled deployment-specific location' if valid_path else 'untrusted backup path')
    if not valid_path:
        return result
    good_size = expected.is_file() and 0 < expected.stat().st_size <= MAX_BYTES
    check('backup-size', good_size, 'bounded backup size' if good_size else 'missing/empty/oversize backup')
    if not good_size:
        return result
    # Copy first, then HASH THE COPY, so all subsequent tests operate on the same bytes.
    with tempfile.TemporaryDirectory(prefix='dc-recovery-v19-') as temp:
        clone = Path(temp) / 'snapshot.db'
        shutil.copyfile(expected, clone)
        with clone.open('rb') as fd:
            digest = hashlib.file_digest(fd, 'sha256').hexdigest().upper()
        ok_hash = (isinstance(record.get('BackupSha256'), str) and
                   digest == record['BackupSha256'].upper())
        check('sha256', ok_hash, 'snapshot hash matches journal' if ok_hash else 'backup checksum mismatch')
        if not ok_hash:
            return result
        try:
            uri = 'file:' + quote(str(clone)) + '?mode=ro&immutable=1'
            with closing(sqlite3.connect(uri, uri=True)) as db:
                intact = db.execute('PRAGMA integrity_check').fetchone()[0] == 'ok'
                check('sqlite-integrity', intact, 'SQLite integrity check' if intact else 'SQLite corruption')
                if intact:
                    frow = db.execute('SELECT version_id FROM active_workflow WHERE singleton=1').fetchone()
                    rrow = db.execute('SELECT id FROM active_recipe WHERE singleton=1').fetchone()
                    result['backupFlowId'] = frow[0] if frow else None
                    result['backupRecipeId'] = rrow[0] if rrow else None
                    check('baseline', result['backupFlowId'] == before_flow.get('Id') and
                          result['backupRecipeId'] == before_recipe.get('Id'),
                          'snapshot active versions match original deployment baseline')
        except (sqlite3.DatabaseError, ValueError) as ex:
            check('sqlite-integrity', False, type(ex).__name__)
    result['backupValidated'] = bool(result['checks']) and all(x['passed'] for x in result['checks'])
    return result

if __name__ == '__main__':
    p = argparse.ArgumentParser(description='Isolated read-only deployment backup validation')
    p.add_argument('--data-dir', type=Path, required=True)
    p.add_argument('--deployment-id', required=True)
    args = p.parse_args()
    try:
        report = inspect(args.data_dir, args.deployment_id)
        print(json.dumps(report, ensure_ascii=False, indent=2))
    except (ValueError, FileNotFoundError, OSError, json.JSONDecodeError) as exc:
        p.exit(2, 'FAILED: ' + str(exc) + '\n')
