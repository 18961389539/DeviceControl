"""Shared isolated .NET test server lifecycle. Compiles once per release output and runs the DLL directly."""
import os
import signal
import shutil
import socket
import subprocess
import time
from urllib.parse import urlparse
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'backend' / 'DeviceControl.Api' / 'bin' / 'Release' / 'net10.0' / 'DeviceControl.Api.dll'

def _ensure_dll():
    source_files = list((ROOT / 'backend' / 'DeviceControl.Api').rglob('*.cs')) + [
        ROOT / 'backend' / 'DeviceControl.Api' / 'DeviceControl.Api.csproj']
    source_mtime = max(p.stat().st_mtime for p in source_files if p.exists())
    if not DLL.exists() or DLL.stat().st_mtime < source_mtime:
        subprocess.run(['dotnet', 'build', 'backend/DeviceControl.Api/DeviceControl.Api.csproj',
                        '-c', 'Release', '--nologo'], cwd=ROOT, check=True)

def start_api(root, env):
    _ensure_dll()
    flags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == 'nt' else 0
    proc = subprocess.Popen(['dotnet', str(DLL)], cwd=root, env=env,
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT,
                            creationflags=flags, start_new_session=(os.name != 'nt'))
    endpoint = urlparse(env.get('SimListenUrl', 'http://127.0.0.1:5088'))
    deadline = time.monotonic() + 60
    try:
        while time.monotonic() < deadline:
            if proc.poll() is not None:
                raise RuntimeError(f'.NET API process exited during startup ({proc.returncode})')
            try:
                with socket.create_connection((endpoint.hostname, endpoint.port), timeout=.25):
                    return proc
            except OSError:
                time.sleep(.1)
        raise TimeoutError('Timed out waiting for isolated .NET API listener')
    except Exception:
        stop_api(proc)
        raise

def stop_api(proc, timeout=12):
    if proc.poll() is not None:
        proc.wait()
        return
    try:
        if os.name == 'nt':
            proc.send_signal(signal_ctrl_break())
        else:
            proc.terminate()
        proc.wait(timeout=timeout)
    except Exception:
        if os.name == 'nt':
            subprocess.run(['taskkill', '/PID', str(proc.pid), '/T', '/F'],
                           check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else:
            try: os.killpg(os.getpgid(proc.pid), 9)
            except ProcessLookupError: pass
        proc.wait(timeout=timeout)

def signal_ctrl_break():
    return getattr(signal, 'CTRL_BREAK_EVENT', signal.SIGTERM)
