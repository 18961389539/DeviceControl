"""Runs the exact SQL schema embedded in SqliteStore.cs using sqlite3.
Does not compile or execute the C# persistence layer."""
import re, sqlite3, tempfile
from pathlib import Path
from contextlib import closing
ROOT=Path(__file__).resolve().parents[1]
code=(ROOT/'backend/DeviceControl.Api/SqliteStore.cs').read_text(encoding='utf8')
match=re.search(r'cmd\.CommandText\s*=\s*"""\s*(CREATE TABLE IF NOT EXISTS schema_version.*?INSERT OR IGNORE INTO schema_version.*?;)\s*"""',code,re.S)
assert match,'Embedded C# schema was not found'
sql=match.group(1)
with tempfile.TemporaryDirectory() as d:
  file=Path(d)/'data.db'
  with closing(sqlite3.connect(file)) as db:
    db.execute('PRAGMA foreign_keys=ON')
    db.execute('PRAGMA journal_mode=WAL')
    db.executescript(sql)
    db.executescript(sql)
    assert db.execute('SELECT COUNT(*) FROM schema_version').fetchone()[0]==4
    tables={row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    assert {'workflow_revisions','active_workflow','recipe_revisions','active_recipe','production_runs','audit_events','schema_version','run_node_traces','workflow_quality_reports'}<=tables
    db.execute("INSERT INTO workflow_revisions VALUES (?,?,?,?,?,?,?,?)",('flow-0001','Published',None,'system','2026-01-01','sha','[]','seed'))
    db.execute("INSERT INTO active_workflow VALUES (1,'flow-0001')")
    db.execute("INSERT INTO workflow_revisions VALUES (?,?,?,?,?,?,?,?)",('flow-0002','Validated','flow-0001','engineer','2026-01-02','sha2','[]','validated'))
    with db:
      db.execute("UPDATE workflow_revisions SET status='Superseded' WHERE id='flow-0001'")
      db.execute("UPDATE workflow_revisions SET status='Published' WHERE id='flow-0002'")
      db.execute("UPDATE active_workflow SET version_id='flow-0002'")
    assert db.execute('SELECT version_id FROM active_workflow').fetchone()[0]=='flow-0002'
    try:
      with db:db.execute("INSERT INTO active_workflow VALUES(2,'missing')")
    except sqlite3.IntegrityError:pass
    else:raise AssertionError('CHECK/FK should have rejected bad active workflow')
    db.execute("INSERT INTO production_runs VALUES(?,?,?,?,?,?,?,?,?)",('r1','2026-01-01','2026-01-01','Completed',45,'flow-0002',None,None,'{}'))
    db.execute("INSERT INTO audit_events VALUES(?,?,?,?,?,?,?,?,?)",('a1','2026-01-01','admin','Admin','POST','/api/flows/flow-0002/publish',200,'abc','{}'))
    db.execute('INSERT INTO run_node_traces VALUES (?,?,?,?,?,?,?,?,?,?,?)',('r1',0,'node-1',None,'等待','WaitInput','Completed',20,1,'2026-01-01',None))
    assert db.execute('SELECT duration_ms FROM run_node_traces WHERE run_id=?',('r1',)).fetchone()[0]==20
    try:
      db.execute('INSERT INTO run_node_traces VALUES (?,?,?,?,?,?,?,?,?,?,?)',('missing',0,'node-2',None,'非法','Delay','Completed',-1,1,'2026-01-01',None))
    except sqlite3.IntegrityError:pass
    else:raise AssertionError('FK/CHECK did not reject invalid trace')
    assert db.execute('SELECT COUNT(*) FROM production_runs').fetchone()[0]==1
    assert db.execute('SELECT COUNT(*) FROM audit_events').fetchone()[0]==1
print('PASS V0.6 SQLite embedded schema: idempotence, version transition, FK/CHECK, run, audit')
