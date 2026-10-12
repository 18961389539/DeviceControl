"""Offline recovery sandbox behavioral tests. Run with standard-library Python 3.11+."""
import hashlib, json, sqlite3, tempfile
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from tools.recovery_sandbox import inspect
import contextlib

DEP='dep-'+'e'*32

def run():
 with tempfile.TemporaryDirectory(prefix='dc-v19-test-') as tmp:
  data=Path(tmp);backups=data/'deployments'/'backups'/'A';backups.mkdir(parents=True)
  db=backups/(DEP+'.db');journal=data/'deployments'/(DEP+'.json')
  with contextlib.closing(sqlite3.connect(db)) as con:
   con.executescript('''CREATE TABLE active_workflow(singleton INTEGER,version_id TEXT);
   CREATE TABLE active_recipe(singleton INTEGER,id TEXT);
   INSERT INTO active_workflow VALUES(1,'flow-before');
   INSERT INTO active_recipe VALUES(1,'recipe-before');''')
  payload={'Id':DEP,'StationId':'A','BackupFile':f'backups/A/{DEP}.db',
   'BeforeFlow':{'Id':'flow-before'},'BeforeRecipe':{'Id':'recipe-before'},
   'BackupSha256':hashlib.sha256(db.read_bytes()).hexdigest().upper()}
  def save():journal.write_text(json.dumps(payload))
  save();original_db=db.read_bytes();original_journal=journal.read_bytes()
  result=inspect(data,DEP)
  assert result['backupValidated'],result
  assert result['readOnly'] and not result['writesProduction'] and result['requiresManualReview']
  assert (db.read_bytes(),journal.read_bytes())==(original_db,original_journal)
  assert not any(data.glob('recovery-sandboxes/*'))
  payload['BackupSha256']='A'*64;save()
  result=inspect(data,DEP);assert not result['backupValidated'] and result['checks'][-1]['code']=='sha256'
  payload['BackupSha256']=hashlib.sha256(db.read_bytes()).hexdigest().upper()
  payload['BackupFile']='../../../../outside.db';save()
  result=inspect(data,DEP);assert not result['backupValidated'] and not result['checks'][0]['passed']
  payload['BackupFile']=f'backups/A/{DEP}.db';payload['BeforeFlow']['Id']='wrong';save()
  result=inspect(data,DEP);assert not result['backupValidated'] and result['checks'][-1]['code']=='baseline'
  payload['BeforeFlow']['Id']='flow-before';db.write_bytes(b'not a SQLite backup!')
  payload['BackupSha256']=hashlib.sha256(db.read_bytes()).hexdigest().upper();save()
  result=inspect(data,DEP);assert not result['backupValidated'] and not result['checks'][-1]['passed']
  assert db.read_bytes()==b'not a SQLite backup!'
  assert journal.read_bytes()==json.dumps(payload).encode()
  print('PASS V1.9 sandbox: valid snapshot, SHA corruption, traversal, baseline mismatch, non-SQLite, unchanged source')

if __name__=='__main__':run()
