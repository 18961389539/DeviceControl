"""V1.0 SQLite engine-only validation, including v3->v4, atomic evidence,
crash marker semantics and immutable node snapshots. Does NOT execute C#.
"""
from pathlib import Path
import sqlite3,hashlib,json,tempfile,re
import contextlib
ROOT=Path(__file__).resolve().parents[1]
source=(ROOT/'backend/DeviceControl.Api/SqliteStore.cs').read_text(encoding='utf-8')
match=re.search(r'cmd\.CommandText\s*=\s*"""\s*(CREATE TABLE IF NOT EXISTS schema_version.*?INSERT OR IGNORE INTO schema_version.*?;)\s*"""',source,re.S)
assert match,'Unable to extract actual C# schema'
v4=match.group(1)
v3=(ROOT/'tests/fixtures/schema_v3.sql').read_text(encoding='utf-8')
with tempfile.TemporaryDirectory(prefix='dc-v10-') as tmp:
 with contextlib.closing(sqlite3.connect(str(Path(tmp)/'control.db'))) as db:
  db.execute('PRAGMA foreign_keys=ON')
  db.executescript(v3)
  db.execute('INSERT INTO workflow_revisions VALUES (?,?,?,?,?,?,?,?)',('flow-a','Published',None,'system','t0','sha','[]','initial'))
  db.execute('INSERT INTO active_workflow VALUES (1,?)',('flow-a',))
  db.execute('INSERT INTO production_runs VALUES (?,?,?,?,?,?,?,?,?)',('old-cycle','t0','t1','Completed',300,'flow-a',None,None,'{}'))
  db.commit()
  db.executescript(v4);db.executescript(v4)
  assert [v[0] for v in db.execute('SELECT version FROM schema_version ORDER BY version')]==[1,2,3,4]
  assert db.execute('SELECT count(*) FROM production_runs WHERE id="old-cycle"').fetchone()[0]==1
  assert db.execute('SELECT version_id FROM active_workflow').fetchone()[0]=='flow-a'
  # Completed run, exact trace evidence and snapshot hash
  db.execute('INSERT INTO production_runs VALUES (?,?,?,?,?,?,?,?,?)',('run-a','t0','t1','Completed',40,'flow-a',None,None,'{}'))
  db.execute('INSERT INTO run_node_traces VALUES (?,?,?,?,?,?,?,?,?,?,?)',('run-a',0,'axis-x',None,'X轴','AxisMove','Completed',30,1,'t1',None))
  obj={'ordinal':0,'nodeId':'axis-x','capturedUtc':'t1','inputs':{'PartPresent':True}}
  payload=json.dumps(obj,separators=(',',':'),sort_keys=True)
  digest=hashlib.sha256(payload.encode()).hexdigest().upper()
  db.execute('INSERT INTO run_variable_snapshots VALUES (?,?,?,?,?,?)',('run-a',0,'axis-x','t1',payload,digest))
  db.commit()
  saved=db.execute('SELECT payload_json,sha256 FROM run_variable_snapshots WHERE run_id=?',('run-a',)).fetchone()
  assert hashlib.sha256(saved[0].encode()).hexdigest().upper()==saved[1]
  for query in ["UPDATE run_variable_snapshots SET node_id='bad' WHERE run_id='run-a'", "DELETE FROM run_variable_snapshots WHERE run_id='run-a'"]:
   try:db.execute(query)
   except sqlite3.IntegrityError:pass
   else:raise AssertionError('snapshot immutability trigger missing')
  try:
   db.execute('INSERT INTO run_variable_snapshots VALUES (?,?,?,?,?,?)',('run-a',17,'missing','t1','{}','bad'))
  except sqlite3.IntegrityError:pass
  else:raise AssertionError('trace FK must reject orphan snapshot')
  assert db.execute('SELECT count(*) FROM run_variable_snapshots').fetchone()[0]==1
  # Simulate process crash after BeginCycle. DB leaves a marker, not false Completed.
  db.execute('INSERT INTO run_inflight VALUES (?,?,?,?)',('run-crash','t2','flow-a','recipe-a'))
  db.commit()
  assert db.execute('SELECT run_id FROM run_inflight').fetchone()[0]=='run-crash'
  # Exact DB transaction strategy of SqliteStore.RecoverInterruptedCycles()
  pending=db.execute('SELECT run_id,started_utc,flow_version,recipe_id FROM run_inflight').fetchall()
  with db:
   for r in pending:
    row={'id':r[0],'startedAt':r[1],'outcome':'Interrupted','flowVersion':r[2],'recipeId':r[3]}
    db.execute('INSERT OR IGNORE INTO production_runs VALUES(?,?,?,?,?,?,?,?,?)',(r[0],r[1],'t3','Interrupted',0,r[2],r[3],'process exited',json.dumps(row)))
   db.execute('DELETE FROM run_inflight')
  assert db.execute('SELECT outcome FROM production_runs WHERE id="run-crash"').fetchone()[0]=='Interrupted'
  assert db.execute('SELECT count(*) FROM run_inflight').fetchone()[0]==0
  assert db.execute('PRAGMA foreign_key_check').fetchall()==[]
  assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
  # Snapshot data and trace are atomic; failed FK must roll back all changes.
  try:
   with db:
    db.execute('INSERT INTO production_runs VALUES (?,?,?,?,?,?,?,?,?)',('run-failed','t0','t1','Completed',1,None,None,None,'{}'))
    db.execute('INSERT INTO run_variable_snapshots VALUES (?,?,?,?,?,?)',('run-failed',0,'bad','t1','{}','sha'))
  except sqlite3.IntegrityError:pass
  else:raise AssertionError('test expected FK rejection')
  assert db.execute('SELECT count(*) FROM production_runs WHERE id="run-failed"').fetchone()[0]==0
print('PASS V1.0 SQLite: v3->v4 idempotent migration, FK, immutable snapshots, recovery markers, atomic rollback')
