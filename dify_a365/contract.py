"""Small, standard-library-only file and configuration contracts. No network."""
import hashlib
import json
import os
import re
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit
from uuid import UUID, uuid4

CONTEXT_KEYS = ('tenant_id', 'blueprint_id', 'client_id', 'operator_id', 'workspace_id')
FILTERS = {'created_by_me': False, 'mode': 'all', 'name': None, 'source_ids': None}
MODES = {'chat', 'advanced-chat', 'agent-chat', 'workflow', 'completion'}


class LabError(RuntimeError):
    pass


def need(condition, reason):
    if not condition:
        raise LabError(reason)


def guid(value):
    try:
        return isinstance(value, str) and str(UUID(value)) == value and UUID(value).int != 0
    except ValueError:
        return False


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()


def file_sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def _pairs(pairs):
    result = {}
    for key, value in pairs:
        need(key not in result, 'duplicate_json_key')
        result[key] = value
    return result


def read_json(path):
    path = Path(path)
    need(path.is_file() and not path.is_symlink() and path.stat().st_size <= 2_000_000, 'input_file_size_or_path')
    try:
        return json.loads(path.read_text(encoding='utf-8-sig'), object_pairs_hook=_pairs,
                          parse_constant=lambda _: need(False, 'nonfinite_json'))
    except (ValueError, UnicodeError):
        raise LabError('invalid_json') from None


def save_json(path, value):
    """Create-only durable receipts, with restrictive POSIX permissions."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(value, indent=2, ensure_ascii=True, allow_nan=False) + '\n'
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'w', encoding='utf-8', newline='\n') as stream:
        stream.write(text)
        stream.flush()
        os.fsync(stream.fileno())


def replace_json(path, value):
    """Only for the local binding cache, while the caller holds the apply marker."""
    path = Path(path)
    need(not path.is_symlink(), 'binding_symlink')
    temporary = path.with_name(path.name + '.' + uuid4().hex + '.tmp')
    save_json(temporary, value)
    os.replace(temporary, path)


def origin(value):
    need(isinstance(value, str), 'dify_origin')
    parsed = urlsplit(value)
    need(parsed.scheme in ('https', 'http') and parsed.hostname and not parsed.username and not parsed.password
         and not parsed.query and not parsed.fragment and parsed.path in ('', '/'), 'dify_origin')
    need(parsed.scheme == 'https' or parsed.hostname in ('localhost', '127.0.0.1', '::1'), 'https_or_loopback_required')
    canonical = f'{parsed.scheme}://{parsed.netloc}'.rstrip('/')
    need(value == canonical, 'canonical_dify_origin_required')
    return canonical


def validate_config(value):
    need(isinstance(value, dict) and set(value) == {'schema', 'dify_origin', 'operator_upn', *CONTEXT_KEYS}, 'config_fields')
    need(type(value['schema']) is int and value['schema'] == 1, 'config_schema')
    need(all(guid(value[k]) for k in CONTEXT_KEYS), 'config_context_ids')
    need(len({value[k] for k in CONTEXT_KEYS}) == len(CONTEXT_KEYS), 'config_distinct_ids')
    origin(value['dify_origin'])
    upn = value['operator_upn']
    need(isinstance(upn, str) and re.fullmatch(r'[^\s@\x00-\x1f]+@[^\s@\x00-\x1f]+', upn)
         and len(upn) <= 254, 'operator_upn')
    return value


def context(config):
    return {k: config[k] for k in CONTEXT_KEYS}


def timestamp(value):
    need(isinstance(value, str), 'timestamp')
    try:
        result = datetime.fromisoformat(value.replace('Z', '+00:00'))
    except ValueError:
        raise LabError('timestamp') from None
    need(result.tzinfo is not None, 'timestamp_timezone')
    return result.astimezone(timezone.utc)


def utc_text():
    return datetime.now(timezone.utc).isoformat()


def validate_inventory(value, config):
    need(isinstance(value, dict) and value.get('schema') == 1, 'inventory_schema')
    need(value.get('complete') is True and value.get('credentials_returned') is False, 'inventory_incomplete_or_sensitive')
    need(value.get('instance_url') == config['dify_origin'] and value.get('workspace_id') == config['workspace_id'], 'inventory_source_binding')
    need(value.get('selection_filters') == FILTERS, 'filtered_discovery')
    timestamp(value.get('observed_at_utc'))
    apps = value.get('apps')
    need(isinstance(apps, list) and 1 <= len(apps) <= 5 and type(value.get('total')) is int and value['total'] == len(apps), 'lab_requires_one_to_five_apps')
    for app in apps:
        need(isinstance(app, dict) and set(app) == {'id', 'name', 'mode'} and guid(app['id']), 'app_metadata')
        need(isinstance(app['name'], str) and 1 <= len(app['name']) <= 256 and app['name'].strip()
             and not any(ord(c) < 32 or ord(c) == 127 for c in app['name']), 'app_name')
        need(app['mode'] in MODES, 'app_mode')
    need(len({a['id'] for a in apps}) == len(apps), 'duplicate_source_id')
    need(len({a['name'].strip().casefold() for a in apps}) == len(apps), 'duplicate_source_name')
    pages = value.get('pages')
    need(isinstance(pages, list) and 1 <= len(pages) <= 20, 'inventory_pages')
    count = 0
    for number, page in enumerate(pages, 1):
        need(isinstance(page, dict) and page.get('page') == number and page.get('limit') == 100
             and page.get('total') == len(apps) and type(page.get('count')) is int
             and 0 <= page['count'] <= 100 and page.get('has_more') is (number < len(pages)), 'inventory_pagination')
        count += page['count']
    need(count == len(apps), 'inventory_page_coverage')
    return value
