"""Process bridge. No process, credentials, or network access at import time."""
import json
import queue
import re
import shutil
import subprocess
import threading
import time
from pathlib import Path
from uuid import uuid4

from .contract import LabError, context, file_sha, need, read_json, save_json

PACKAGE_VERSION = '1.1.221'
REPO = Path(__file__).resolve().parents[1]
OUTPUT = REPO / 'native/bin/Release/net8.0'


def runtime_hashes():
    need((OUTPUT / 'WrapperNative.dll').is_file(), 'native_not_built: run python lab.py build')
    paths = sorted(p for p in OUTPUT.iterdir() if p.is_file())
    need(not any(p.is_symlink() for p in paths), 'native_symlink')
    return {p.name: file_sha(p) for p in paths}


def build(cli_package=None):
    package = Path(cli_package).resolve() if cli_package else (
        REPO / '.tools/a365/.store/microsoft.agents.a365.devtools.cli' / PACKAGE_VERSION /
        'microsoft.agents.a365.devtools.cli' / PACKAGE_VERSION / 'tools/net8.0/any')
    need((package / 'Microsoft.Agents.A365.DevTools.Cli.dll').is_file(),
         'pinned_cli_missing: follow README installation instructions')
    dotnet = shutil.which('dotnet')
    need(dotnet, 'dotnet_8_sdk_required')
    result = subprocess.run([dotnet, 'build', str(REPO / 'native/WrapperNative.csproj'),
                             '--configuration', 'Release', '--nologo', '-p:CliPackage=' + str(package)],
                            cwd=REPO, check=False)
    need(result.returncode == 0, 'native_build_failed')
    return runtime_hashes()


class NativeClient:
    def __init__(self, config, targets, directory, scope, run_id, *, writes=False,
                 reference='operator-lab', approved=False, notify=print, popen=subprocess.Popen):
        self.lab = config
        self.context_value = context(config)
        self.directory = Path(directory).resolve()
        self.directory.mkdir(parents=True, exist_ok=True)
        self.process = None
        self.thread = None
        self.started = False
        self.failed = False
        self.approved = approved
        self.notify = notify
        self.popen = popen
        self.queue = queue.Queue(maxsize=72)
        self.scope = scope
        self.dotnet = shutil.which('dotnet')
        need(self.dotnet, 'dotnet_runtime_required')
        self.dotnet_hash = file_sha(self.dotnet)
        self.hashes = runtime_hashes()
        self.config_path = self.directory / 'native-config.json'
        self.reference = reference
        native_targets = [{k: target[k] for k in ('key', 'source_id', 'identity_name', 'identity_id',
                                                  'registration_id', 'registration_name')} for target in targets]
        save_json(self.config_path, {'schema': 1, **self.context_value, 'operator_upn': config['operator_upn'],
                  'scope_sha256': scope, 'approval_reference': reference, 'pass_ids': [run_id],
                  'allow_writes': writes, 'targets': native_targets,
                  'journal_directory': str(self.directory / 'journal')})
        self.config_hash = file_sha(self.config_path)

    def check(self):
        need(file_sha(self.config_path) == self.config_hash and runtime_hashes() == self.hashes
             and file_sha(self.dotnet) == self.dotnet_hash, 'native_artifact_drift')

    def receive(self, timeout):
        try:
            frame = self.queue.get(timeout=max(0.001, timeout))
        except queue.Empty:
            self.failed = True
            raise LabError('native_response_timeout') from None
        need(isinstance(frame, dict), 'native_stream_ended')
        if frame.get('status') == 'stopped':
            self.failed = True
            # Native host emits only allowlisted diagnostic categories, never raw HTTP bodies.
            reason = frame.get('reason', 'native_session_stopped')
            need(isinstance(reason, str) and re.fullmatch(r'[a-z_]{1,80}', reason), 'native_session_stopped')
            raise LabError(reason)
        return frame

    def start(self):
        need(self.approved is True and not self.started, 'native_authentication_not_approved')
        self.check()
        save_json(self.directory / 'client-started.json', {
            'operation': 'native_authentication_session', 'config_sha256': self.config_hash,
            'scope_sha256': self.scope, 'approval_reference': self.reference})
        self.process = self.popen([self.dotnet, str(OUTPUT / 'WrapperNative.dll'), '--live',
                                  '--ack-stop-on-consent', '--config', str(self.config_path),
                                  '--sha256', self.config_hash, '--approval-reference', self.reference],
                                 cwd=REPO, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                 stderr=subprocess.DEVNULL, text=True, encoding='utf-8', bufsize=1)

        def reader():
            try:
                for _ in range(72):
                    line = self.process.stdout.readline(16385)
                    if not line or len(line) > 16384:
                        self.queue.put_nowait(None)
                        return
                    self.queue.put_nowait(json.loads(line))
            except Exception:
                try:
                    self.queue.put_nowait(None)
                except queue.Full:
                    pass

        self.thread = threading.Thread(target=reader, daemon=True)
        self.thread.start()
        deadline = time.monotonic() + 620
        device_seen = False
        while time.monotonic() < deadline:
            frame = self.receive(deadline - time.monotonic())
            if frame.get('status') == 'device_code_ready':
                need(not device_seen, 'duplicate_device_prompt')
                device_seen = True
                uri, code = frame.get('verification_uri'), frame.get('user_code')
                need(uri in ('https://login.microsoft.com/device', 'https://microsoft.com/devicelogin',
                             'https://www.microsoft.com/devicelogin') and isinstance(code, str)
                     and re.fullmatch(r'[A-Z0-9-]{6,20}', code), 'device_code_shape')
                self.notify(f'Sign in at {uri} using code {code}. Expected account: {self.lab["operator_upn"]}. STOP on any consent request or wrong account/application.')
                continue
            expected = {'status': 'auth_ready', 'tenant_id': self.lab['tenant_id'],
                        'client_id': self.lab['client_id'], 'operator_id': self.lab['operator_id'],
                        'scope_sha256': self.scope}
            need(frame == expected, 'native_auth_context')
            self.started = True
            save_json(self.directory / 'auth-verified.json', frame)
            return
        raise LabError('native_auth_timeout')

    def exchange(self, method, **fields):
        need(not self.failed, 'native_client_stopped')
        try:
            if not self.started:
                self.start()
            self.check()
            request_id = str(uuid4())
            self.process.stdin.write(json.dumps({'request_id': request_id, 'method': method, **fields}) + '\n')
            self.process.stdin.flush()
            response = self.receive(60)
            need(set(response) == {'status', 'request_id', 'method', 'result'} and
                 response['status'] == 'native_response' and response['request_id'] == request_id and
                 response['method'] == method, 'native_response_binding')
            save_json(self.directory / (request_id + '-response.json'), response)
            return response['result']
        except Exception:
            self.failed = True
            raise

    def context(self): return self.exchange('context')
    def get_identity(self, iid): return self.exchange('get_identity', id=iid)
    def inspect_identity(self, iid): return self.exchange('inspect_identity', id=iid)
    def get_registration(self, rid): return self.exchange('get_registration', id=rid)
    def find_identity(self, name): return self.exchange('find_identity', name=name)
    def create_identity(self, action): return self.exchange('create_identity', action=action)
    def register(self, action): return self.exchange('register_agent', action=action)

    def close(self):
        if self.process is None:
            return None
        try:
            self.process.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            result = self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
            result = self.process.wait(timeout=5)
        if self.thread is not None:
            self.thread.join(timeout=1)
        return result
