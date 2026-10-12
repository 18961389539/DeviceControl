"""V2.0 regression: backup provenance and symlink controls on isolated fixtures."""
import hashlib
import json
import sqlite3
import tempfile
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from tools.recovery_sandbox import inspect
import contextlib

ID='dep-'+'a'*32

def write_snapshot(db):
    db.parent.mkdir(parents=True, exist_ok=True)
    with contextlib.closing(sqlite3.connect(db)) as conn:
        conn.executescript('''CREATE TABLE active_workflow(singleton INTEGER PRIMARY KEY,version_id TEXT);
        CREATE TABLE active_recipe(singleton INTEGER PRIMARY KEY,id TEXT);
        INSERT INTO active_workflow VALUES(1,'flow-1');
        INSERT INTO active_recipe VALUES(1,'recipe-1');''')

def run():
    with tempfile.TemporaryDirectory(prefix='devicecontrol-v20-') as temp:
        root=Path(temp)
        parent=root/'data'/'deployments'
        a=parent/'backups'/'A'/f'{ID}.db'
        write_snapshot(a)
        journal=parent/f'{ID}.json'
        record={'Id':ID,'StationId':'A', 'BeforeFlow':{'Id':'flow-1'},
                'BeforeRecipe':{'Id':'recipe-1'},
                'BackupFile':f'backups/A/{ID}.db',
                'BackupSha256':hashlib.sha256(a.read_bytes()).hexdigest().upper()}
        def save(): journal.write_text(json.dumps(record))
        save()
        before=(a.read_bytes(),journal.read_bytes())
        outcome=inspect(root/'data',ID)
        assert outcome['backupValidated'] and all(x['passed'] for x in outcome['checks']),outcome
        assert (a.read_bytes(),journal.read_bytes())==before
        # Attacker alters only journal to borrow backup of a different station.
        b=parent/'backups'/'B'/f'{ID}.db';b.parent.mkdir(parents=True)
        b.write_bytes(a.read_bytes())
        record['BackupFile']=f'backups/B/{ID}.db';save()
        bad=inspect(root/'data',ID)
        assert not bad['backupValidated'] and bad['checks'][0]['code']=='backup-path' and not bad['checks'][0]['passed']
        record['BackupFile']=f'backups/A/{ID}.db';save()
        # Symlink with identical bytes & digest must be rejected, not followed.
        a.unlink();a.symlink_to(b)
        bad=inspect(root/'data',ID)
        assert not bad['backupValidated'] and not bad['checks'][0]['passed']
        a.unlink()
        # Parent directory symlink is also a provenance breach.
        a.parent.rmdir();a.parent.symlink_to(b.parent,target_is_directory=True)
        bad=inspect(root/'data',ID)
        assert not bad['backupValidated'] and not bad['checks'][0]['passed']
        a.parent.unlink()
        # Escaping via the backups directory itself is forbidden.
        (parent/'backups'/'B'/f'{ID}.db').unlink()
        (parent/'backups'/'B').rmdir()
        (parent/'backups').rmdir()
        external=root/'external';(external/'A').mkdir(parents=True)
        write_snapshot(external/'A'/f'{ID}.db')
        (parent/'backups').symlink_to(external,target_is_directory=True)
        bad=inspect(root/'data',ID)
        assert not bad['backupValidated'] and not bad['checks'][0]['passed']
        assert journal.read_bytes()==json.dumps(record).encode()
    print('PASS V2.0 sandbox provenance: valid copy, cross-station substitution, file/directory symlinks; no production writes')

if __name__=='__main__':run()
