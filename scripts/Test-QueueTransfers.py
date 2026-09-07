"""Real HTTP partial transfers through the same queue transport source and aria2c."""
import argparse, hashlib, http.server, json, pathlib, re, subprocess, sys, threading, time

root = pathlib.Path(__file__).resolve().parents[1]
arguments = argparse.ArgumentParser()
arguments.add_argument('--output', type=pathlib.Path)
options = arguments.parse_args()
artifact = options.output or root / 'artifacts/queue-validation' / ('transfers-' + time.strftime('%Y%m%d-%H%M%S'))
artifact.mkdir(parents=True, exist_ok=False)
harness = root / 'tests/QueueTransferHarness/bin/Release/net8.0/QueueTransferHarness.dll'
aria = root / 'artifacts/queue-validation/tools-build/tools/aria2/aria2c.exe'
if not aria.exists():
    found = list((root / 'artifacts/queue-validation/tools-build/tools').rglob('aria2c.exe'))
    assert len(found) == 1
    aria = found[0]
data = bytes(range(256)) * (48 * 1024 * 1024 // 256 + 7)
expected = hashlib.sha256(data).hexdigest().upper()
requests = []
state = {'etag': '"resource1"', 'ignore': False, 'bad_range': False, 'slow': True}

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def do_GET(self):
        match = re.fullmatch(r'bytes=(\d+)-(\d*)', self.headers.get('Range', ''))
        start, end = (int(match[1]), int(match[2]) if match[2] else len(data)-1) if match else (0, len(data)-1)
        end = min(end, len(data)-1)
        requests.append((self.path, start, end))
        if state['ignore']: start, end = 0, len(data)-1
        self.send_response(206 if match and not state['ignore'] else 200)
        self.send_header('Content-Length', str(end-start+1))
        self.send_header('Accept-Ranges', 'bytes')
        self.send_header('ETag', state['etag'])
        if match and not state['ignore']:
            self.send_header('Content-Range', f"bytes {start + (1 if state['bad_range'] else 0)}-{end}/{len(data)}")
        self.end_headers()
        try:
            for pos in range(start, end+1, 65536):
                self.wfile.write(data[pos:min(end+1,pos+65536)])
                if end-start > 65536 and state['slow']: time.sleep(.006)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass

server = http.server.ThreadingHTTPServer(('127.0.0.1',0), Handler)
threading.Thread(target=server.serve_forever,daemon=True).start()
url = f'http://127.0.0.1:{server.server_port}/resource.bin'
results = []

def start(path, mode):
    return subprocess.Popen(['dotnet',str(harness),url+'?fresh='+str(time.time_ns()),str(path),mode,str(aria)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT)

def finish(process, success=True):
    output = process.communicate(timeout=60)[0].decode('utf-8',errors='replace')
    if success: assert process.returncode == 0 and 'TRANSFER_VERIFIED' in output, output[-2500:]
    else: assert process.returncode != 0, output
    return output

def partial(path,mode):
    process = start(path,mode)
    deadline = time.monotonic()+20
    while time.monotonic()<deadline:
        if mode=='aria':
            ready=path.exists() and path.stat().st_size>1048576 and pathlib.Path(str(path)+'.aria2').exists()
        else:
            ready=sum(p.stat().st_size for p in path.parent.glob(path.name+'.part-*'))>4*1048576
        if ready: break
        if process.poll() is not None: raise AssertionError(finish(process))
        time.sleep(.05)
    else: raise AssertionError('no partial transfer')
    subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,check=True)
    process.communicate(timeout=10)

try:
    for mode in ('single','multi','aria'):
        path=artifact/(mode+'.bin')
        partial(path,mode)
        before=len(requests)
        finish(start(path,mode))
        assert hashlib.sha256(path.read_bytes()).hexdigest().upper()==expected
        assert any(begin>0 for _,begin,_ in requests[before:]), 'no resumed Range request'
        assert not list(path.parent.glob(path.name+'.part-*')), 'completed transfer retained parts'
        size=path.stat().st_size
        finish(start(path,mode))
        assert path.stat().st_size==size
        results.append({'mode':mode,'resume':True,'hash':expected,'bytes':size,'parts_cleaned':True})
        print(mode+': interrupted, resumed and SHA256 matched',flush=True)
    for mode in ('single', 'multi'):
        path = artifact / (mode+'.bin')
        manifest = json.loads(pathlib.Path(str(path)+'.beflow.json').read_text(encoding='utf-8'))
        parts = []
        for index, part in enumerate(manifest['Parts']):
            temporary = pathlib.Path(str(path)+f'.part-{index:05d}')
            temporary.write_bytes(data[part['From']:part['To']+1])
            parts.append(temporary)
        unrelated = pathlib.Path(str(path)+'.part-99999')
        unrelated.write_bytes(b'not owned by the transfer')
        # A changed part must stop cleanup before any other valid part is deleted.
        last = parts[-1]
        with last.open('r+b') as file: file.write(b'changed')
        hashes = {str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in parts}
        finish(start(path,mode),False)
        assert hashes == {str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in parts}
        last_part = manifest['Parts'][-1]
        last.write_bytes(data[last_part['From']:last_part['To']+1])
        parts[0].unlink()  # Simulate a prior cleanup interrupted after its first deletion.
        finish(start(path,mode))
        assert all(not p.exists() for p in parts)
        assert unrelated.read_bytes() == b'not owned by the transfer'
        assert hashlib.sha256(path.read_bytes()).hexdigest().upper() == expected
        results.append({'mode':mode,'cleanup_restart':True,'changed_part_preserved':True,'unrelated_preserved':True})
        print(mode+': cleanup resumed; changed and unrelated files protected',flush=True)
    for scenario in ('changed','ignore','bad_range','damaged'):
        path=artifact/(scenario+'.bin'); partial(path,'single')
        parts=list(path.parent.glob(path.name+'.part-*'))
        if scenario=='changed': state['etag']='"resource2"'
        if scenario=='ignore': state['ignore']=True
        if scenario=='bad_range': state['bad_range']=True
        if scenario=='damaged':
            with parts[0].open('r+b') as file: file.write(b'corrupt')
        hashes={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in parts}
        finish(start(path,'single'),False)
        assert not path.exists()
        assert hashes=={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in parts}, 'failed resume modified partial bytes'
        state.update(etag='"resource1"',ignore=False,bad_range=False)
        results.append({'scenario':scenario,'rejected_without_truncation':True})
        print(scenario+': rejected, original partial bytes preserved',flush=True)
    (artifact/'results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
finally: server.shutdown()
