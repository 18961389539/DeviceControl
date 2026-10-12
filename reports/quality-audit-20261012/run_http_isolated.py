"""Audit adapter: run unchanged HTTP assertions against the built Release DLL.
Avoid recompiling the user's running Debug executable. Drain API output to a
file, so the first test's unread stdout PIPE cannot block server logging.
No assertions or SQLite cleanup semantics are changed.
"""
import json, os, runpy, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DLL = ROOT / 'backend/DeviceControl.Api/bin/Release/net10.0/DeviceControl.Api.dll'
TESTS = ['test_api_v09.py', 'test_api_v10.py', 'test_api_v12.py',
         'test_api_v13.py', 'test_api_v14.py', 'test_api_v15.py',
         'test_api_v16.py', 'test_api_v17.py', 'test_api_v18.py', 'test_api_v19.py']

def one(name):
    original = subprocess.Popen
    processes = []
    logs = []
    def patched(command, *args, **kwargs):
        if isinstance(command, list) and len(command) > 1 and Path(str(command[0])).stem == 'dotnet' and command[1] == 'run':
            command = [command[0], str(DLL)]
            log = (HERE / 'http-isolated' / (name + '.server.log')).open('ab')
            logs.append(log)
            kwargs['stdout'] = log
            kwargs['stderr'] = subprocess.STDOUT
        proc = original(command, *args, **kwargs)
        processes.append(proc)
        return proc
    subprocess.Popen = patched
    try:
        runpy.run_path(str(ROOT / 'tests' / name), run_name='__main__')
    finally:
        for proc in processes:
            if proc.poll() is None:
                proc.terminate()
                try: proc.wait(timeout=10)
                except subprocess.TimeoutExpired: proc.kill(); proc.wait()
        for log in logs: log.close()

if __name__ == '__main__':
    (HERE / 'http-isolated').mkdir(exist_ok=True)
    if len(sys.argv) > 1:
        one(sys.argv[1])
    else:
        results = []
        for name in TESTS:
            start = time.monotonic()
            with (HERE / 'http-isolated' / (name + '.log')).open('w', encoding='utf-8') as log:
                try:
                    run = subprocess.run([sys.executable, str(__file__), name], cwd=ROOT,
                        stdout=log, stderr=subprocess.STDOUT, timeout=150,
                        env={**os.environ, 'PYTHONUTF8':'1', 'PYTHONUNBUFFERED':'1'})
                    status = 'PASS' if run.returncode == 0 else 'FAIL'
                except subprocess.TimeoutExpired:
                    status = 'TIMEOUT'
            result = {'test':name, 'status':status, 'seconds':round(time.monotonic()-start,2)}
            results.append(result)
            (HERE / 'http-isolated-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
            print(result, flush=True)
