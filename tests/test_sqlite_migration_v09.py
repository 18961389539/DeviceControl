"""Execute the exact V0.9 schema SQL via Python sqlite3, including old v2 DB upgrades.
The SQLite engine is tested; this does not compile or test Microsoft.Data.Sqlite C# methods.
"""
from pathlib import Path
import re,sqlite3,tempfile
root=Path(__file__).resolve().parents[1]
v2=(root/'tests/fixtures/schema_v2.sql').read_text()
v3=(root/'tests/fixtures/schema_v3.sql').read_text()
with tempfile.TemporaryDirectory() as folder:
 with sqlite3.connect(Path(folder)/'test.db') as db:
  db.execute('PRAGMA foreign_keys=ON')
  db.executescript(v2)
  db.execute('INSERT INTO workflow_revisions VALUES (?,?,?,?,?,?,?,?)',('flow-base','Published',None,'system','2026-01-01','old-sha','[]','legacy'))
  db.execute('INSERT INTO active_workflow VALUES(1,?)',('flow-base',))
  db.execute('INSERT INTO production_runs VALUES (?,?,?,?,?,?,?,?,?)',('old-run','2026-01-01','2026-01-01','Completed',100,'flow-base',None,None,'{}'))
  db.commit()
  db.executescript(v3)
  db.executescript(v3)
  assert [r[0] for r in db.execute('SELECT version FROM schema_version ORDER BY version')]==[1,2,3]
  assert db.execute('SELECT version_id FROM active_workflow').fetchone()[0]=='flow-base'
  assert db.execute('SELECT id FROM production_runs').fetchone()[0]=='old-run'
  db.execute('INSERT INTO workflow_revisions VALUES (?,?,?,?,?,?,?,?)',('flow-new','Validated','flow-base','engineer','2026-01-02','sha-new','[]','passed'))
  def insert_gate(flow='flow-new',passed=1):
   db.execute('INSERT INTO workflow_quality_reports VALUES (?,?,?,?,?,?,?)',('gate-'+flow,flow,'sha-new','report-sha',passed,'2026-01-02','{}'))
  try:
   insert_gate('missing')
  except sqlite3.IntegrityError: pass
  else: raise AssertionError('missing workflow FK should reject quality report')
  insert_gate()
  db.commit()
  for sql in ["UPDATE workflow_quality_reports SET passed=0 WHERE flow_id='flow-new'", "DELETE FROM workflow_quality_reports WHERE flow_id='flow-new'"]:
   try: db.execute(sql)
   except sqlite3.IntegrityError: pass
   else: raise AssertionError('immutable quality report unexpectedly changed')
  assert db.execute('SELECT passed FROM workflow_quality_reports WHERE flow_id=?',('flow-new',)).fetchone()[0]==1
  # Transaction proves active pointer rollback on abort.
  try:
   with db:
    db.execute("UPDATE active_workflow SET version_id='flow-new'")
    db.execute("INSERT INTO workflow_quality_reports VALUES ('x','bad','sha','r',1,'t','{}')")
  except sqlite3.IntegrityError: pass
  else: raise AssertionError('expected FK transaction failure')
  assert db.execute('SELECT version_id FROM active_workflow').fetchone()[0]=='flow-base'
  assert db.execute('PRAGMA integrity_check').fetchone()[0]=='ok'
print('PASS V0.9 SQLite v2->v3: idempotent migration, immutable evidence, FK, rollback, legacy data')
