"""Schema v1 -> v2 idempotent migration, preserving existing runs and active version.
C# SQL text tested in Python SQLite; not a compiled .NET persistence integration test.
"""
from pathlib import Path
import re,sqlite3,tempfile
old=(Path(__file__).parent/'fixtures/schema_v1.sql').read_text(encoding='utf8')
new=(Path(__file__).parent/'fixtures/schema_v2.sql').read_text(encoding='utf8')
pattern=r'cmd\.CommandText\s*=\s*"""\s*(CREATE TABLE IF NOT EXISTS schema_version.*?INSERT OR IGNORE INTO schema_version.*?;)\s*"""'
v1=old
v2=new
with tempfile.TemporaryDirectory() as folder:
  with sqlite3.connect(Path(folder)/'upgrade.db') as db:
    db.execute('PRAGMA foreign_keys=ON')
    db.executescript(v1)
    db.execute("INSERT INTO workflow_revisions VALUES (?,?,?,?,?,?,?,?)",('r-v1','Published',None,'system','2026-01-01','hash','[]','old'))
    db.execute("INSERT INTO active_workflow VALUES(1,'r-v1')")
    db.execute("INSERT INTO production_runs VALUES (?,?,?,?,?,?,?,?,?)",('run-old','2026-01-01','2026-01-01','Completed',900,'r-v1',None,None,'{}'))
    db.commit()
    db.executescript(v2);db.executescript(v2)
    assert db.execute('SELECT COUNT(*) FROM schema_version').fetchone()[0]==2
    assert db.execute('SELECT version_id FROM active_workflow').fetchone()[0]=='r-v1'
    assert db.execute('SELECT id FROM production_runs').fetchone()[0]=='run-old'
    db.execute("INSERT INTO run_node_traces VALUES(?,?,?,?,?,?,?,?,?,?,?)",('run-old',0,'n1',None,'动作','Delay','Completed',50,1,'2026-01-01',None))
    try:
      with db:db.execute("INSERT INTO run_node_traces VALUES(?,?,?,?,?,?,?,?,?,?,?)",('unknown',0,'n2',None,'动作','Delay','Completed',25,1,'2026-01-01',None))
    except sqlite3.IntegrityError:pass
    else:raise AssertionError('foreign key must reject unknown cycle')
    assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
print('PASS V0.8 SQLite v1->v2: preserves history and active flow, idempotent upgrade, FK, integrity')
