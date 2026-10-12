#!/usr/bin/env python3
"""Optional simulation SQLite health check + online consistent backup.

Does not modify the source or configure recurring backup policy.
"""
import argparse
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote
import sqlite3
import os
import sys

def inspect(db):
    db.execute('PRAGMA busy_timeout=10000')
    verdict=db.execute('PRAGMA integrity_check').fetchone()[0]
    if verdict!='ok':raise RuntimeError('SQLite integrity_check failed: '+str(verdict))
    violations=db.execute('PRAGMA foreign_key_check').fetchall()
    if violations:raise RuntimeError('Foreign key violations: '+str(violations[:5]))
    tables=[row[0] for row in db.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
    result={}
    for name in tables:
        escaped=name.replace('"','""')
        result[name]=db.execute('SELECT count(*) FROM "'+escaped+'"').fetchone()[0]
    return result

def backup_database(source,target):
    src=Path(source).expanduser().resolve()
    dst=Path(target).expanduser().resolve()
    if not src.is_file():raise FileNotFoundError(src)
    if src==dst:raise ValueError('Source and backup must be different files')
    dst.parent.mkdir(parents=True,exist_ok=True)
    if dst.exists():raise FileExistsError('Backup already exists: '+str(dst))
    partial=dst.with_name(dst.name+'.partial')
    if partial.exists():raise FileExistsError('Partial backup already exists: '+str(partial))
    try:
        uri='file:'+quote(src.as_posix(),safe='/')+'?mode=ro'
        with sqlite3.connect(uri,uri=True,timeout=10) as src_db:
            inspect(src_db)
            with sqlite3.connect(partial,timeout=10) as dst_db:
                src_db.backup(dst_db,pages=100,sleep=.03)
                dst_db.commit()
                # Source may change during online backup; verify the resulting snapshot itself.
                summary=inspect(dst_db)
        os.replace(partial,dst)
    finally:
        if partial.exists():partial.unlink()
    return summary

def main():
    parser=argparse.ArgumentParser(description='Check and back up a simulation SQLite database')
    parser.add_argument('database',type=Path)
    parser.add_argument('--output',type=Path)
    args=parser.parse_args()
    dst=args.output or args.database.with_name(args.database.stem+'.backup-'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.db')
    summary=backup_database(args.database,dst)
    print('Backup integrity OK:',dst)
    print('Table counts:',summary)

if __name__=='__main__':
    try:main()
    except Exception as exc:
        print('Backup failed:',str(exc),file=sys.stderr)
        sys.exit(1)
