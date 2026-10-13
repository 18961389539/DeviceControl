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
    assert db.execute('SELECT COUNT(*) FROM schema_version').fetchone()[0]==5
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
    # v5 migration: explicit audit attribution and command-scoped query columns.
    # The C# MigrateV5 method must contain the matching ALTERs; rows written before
    # the migration keep NULL and are matched by the legacy path heuristic.
    v5_columns={'audit_events':['station_id','command_id','deployment_id'],
                'manual_action_intents':['command_id'],'run_inflight':['command_id'],
                'production_runs':['command_id']}
    for table,columns in v5_columns.items():
        for column in columns:
            assert f'AddColumnIfMissing(cmd, "{table}", "{column}"' in code,(table,column)
            db.execute(f'ALTER TABLE {table} ADD COLUMN {column} TEXT')
    for statement in ('CREATE INDEX IF NOT EXISTS idx_audit_command ON audit_events(command_id)',
                      'CREATE INDEX IF NOT EXISTS idx_audit_station ON audit_events(station_id,time_utc DESC)',
                      'CREATE INDEX IF NOT EXISTS idx_runs_command ON production_runs(command_id)',
                      'CREATE INDEX IF NOT EXISTS idx_intents_command ON manual_action_intents(command_id)'):
        assert statement in code,statement
        db.execute(statement)
    with db:
        db.execute("INSERT INTO audit_events(id,time_utc,actor,role,method,path,http_status,trace_id,payload_json,station_id,command_id,deployment_id)"
                   " VALUES(?,?,?,?,?,?,?,?,?,?,?,?)",('a2','2026-01-01','admin','Admin','POST','/api/upgrades/rollback',200,'t2','{}','B','cmd-x','dep-y'))
        db.execute("INSERT INTO production_runs(id,started_utc,ended_utc,outcome,duration_ms,payload_json,command_id)"
                   " VALUES(?,?,?,?,?,?,?)",('r2','2026-01-01','2026-01-01','Completed',10,'{}','cmd-x'))
    assert db.execute("SELECT station_id,command_id,deployment_id FROM audit_events WHERE id='a2'").fetchone()==('B','cmd-x','dep-y')
    assert db.execute("SELECT command_id FROM production_runs WHERE id='r2'").fetchone()[0]=='cmd-x'
    # Legacy rows keep NULL attribution so the station view falls back to the path rule.
    assert db.execute("SELECT station_id FROM audit_events WHERE id='a1'").fetchone()[0] is None
print('PASS V0.6 SQLite embedded schema: idempotence, version transition, FK/CHECK, run, audit, v5 attribution columns')
