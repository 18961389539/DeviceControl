"""Test real SQLite WAL, rollback, foreign keys and consistent backup.
   SQL schema is extracted from current C# source, but C# itself isn't executed. """
from pathlib import Path
import tempfile,sqlite3,re,sys
from contextlib import closing
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'tools'))
from sqlite_check_backup import backup_database
code=(ROOT/'backend/DeviceControl.Api/SqliteStore.cs').read_text(encoding='utf8')
match=re.search(r'cmd\.CommandText\s*=\s*"""\s*(CREATE TABLE IF NOT EXISTS schema_version.*?INSERT OR IGNORE INTO schema_version.*?;)\s*"""',code,re.S)
assert match
schema=match.group(1)
with tempfile.TemporaryDirectory() as tmp:
    path=Path(tmp)/'station.db';destination=Path(tmp)/'station-copy.db'
    with closing(sqlite3.connect(path)) as db:
        db.execute('PRAGMA journal_mode=WAL')
        db.execute('PRAGMA foreign_keys=ON')
        db.executescript(schema)
        db.execute("INSERT INTO workflow_revisions VALUES(?,?,?,?,?,?,?,?)",('flow-a','Published',None,'system','2026-01-01','hash','[]','initial'))
        db.execute("INSERT INTO active_workflow VALUES(1,'flow-a')")
        db.commit()
        try:
            with db:
                db.execute("INSERT INTO workflow_revisions VALUES(?,?,?,?,?,?,?,?)",('flow-b','Validated','flow-a','engineer','2026-01-02','hash-b','[]','test'))
                db.execute("UPDATE active_workflow SET version_id='missing-version'")
        except sqlite3.IntegrityError:pass
        else:raise AssertionError('Foreign key error should abort transaction')
        assert db.execute("SELECT count(*) FROM workflow_revisions WHERE id='flow-b'").fetchone()[0]==0
        assert db.execute('SELECT version_id FROM active_workflow').fetchone()[0]=='flow-a'
        db.execute('INSERT INTO production_runs VALUES(?,?,?,?,?,?,?,?,?)',('run-a','2026-01-01','2026-01-01','Completed',700,'flow-a',None,None,'{}'))
        db.commit()
        # A backup while the source connection and WAL are still active must produce a standalone valid snapshot.
        counts=backup_database(path,destination)
        assert counts['production_runs']==1
        with closing(sqlite3.connect(destination)) as restored:
            restored.execute('PRAGMA foreign_keys=ON')
            assert restored.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
            assert restored.execute('SELECT version_id FROM active_workflow').fetchone()[0]=='flow-a'
            assert restored.execute('SELECT count(*) FROM production_runs').fetchone()[0]==1
        try:backup_database(path,destination)
        except FileExistsError:pass
        else:raise AssertionError('Backup overwrite should be refused')
print('PASS V0.7 SQLite: WAL-consistent backup, integrity check, FK rollback, restore, overwrite refusal')
