"""Clone-to-lab command host. Explicit commands, bounded operations, no scheduler."""
import argparse
import json
import re
from datetime import datetime, timedelta, timezone
from pathlib import Path
from uuid import uuid4

from .contract import (CONTEXT_KEYS, LabError, context, digest, file_sha, guid, need,
                       read_json, replace_json, save_json, timestamp, utc_text,
                       validate_config, validate_inventory)
from .native import NativeClient, REPO, build, runtime_hashes
from .platform_registration import _identity, _registration, build_targets, reconcile

STATE = REPO / '.local'


def source_key(config):
    return {k: config[k] for k in ('dify_origin', 'workspace_id', 'tenant_id', 'blueprint_id')}


def binding_cache(config, items):
    return {'schema': 1, 'source': source_key(config), 'items': items}


def seeds_for(config, inventory, bindings):
    need(bindings.get('schema') == 1 and bindings.get('source') == source_key(config), 'binding_source_context')
    items = bindings.get('items')
    need(isinstance(items, dict), 'binding_items')
    ids = {app['id'] for app in inventory['apps']}
    need(set(items) <= ids, 'binding_source_missing_from_inventory: no automatic deletion')
    targets = []
    for app in inventory['apps']:
        saved = items.get(app['id'], {})
        need(isinstance(saved, dict), 'binding_item')
        iid, rid = saved.get('identity_id'), saved.get('registration_id')
        name = saved.get('identity_name', app['name'].strip())
        need((iid is None or guid(iid)) and (rid is None or (iid is not None and isinstance(rid, str)
             and re.fullmatch(r'[A-Za-z0-9_-]{1,128}', rid))), 'binding_ids')
        need(isinstance(name, str) and 1 <= len(name) <= 256 and not any(ord(c) < 32 or ord(c) == 127 for c in name), 'binding_name')
        need(iid is not None or name == app['name'].strip(), 'new_identity_name_must_match_source')
        targets.append({'key': 'app' + app['id'].replace('-', ''), 'source_id': app['id'],
                        'source_name': app['name'], 'source_mode': app['mode'], 'identity_name': name,
                        'identity_id': iid, 'registration_id': rid, 'registration_name': app['name'].strip()})
    for field in ('identity_id', 'registration_id'):
        values = [t[field] for t in targets if t[field] is not None]
        need(len(values) == len(set(values)), 'duplicate_binding')
    need(len({t['identity_name'].casefold() for t in targets}) == len(targets), 'duplicate_identity_name')
    return targets


def readonly_preflight(native, targets, config):
    expected = {k: config[k] for k in ('tenant_id', 'blueprint_id', 'client_id', 'operator_id')}
    need(native.context() == expected, 'preflight_native_context')
    rows = []
    for t in targets:
        if t['identity_id'] is None:
            result = native.find_identity(t['identity_name'])
            need(result == {'complete': True, 'items': []}, 'identity_already_exists: supply an explicit verified binding')
            rows.append({'source_id': t['source_id'], 'method': 'find_identity', 'name': t['identity_name'], 'result': result})
        elif t['registration_id'] is None:
            result = native.inspect_identity(t['identity_id'])
            need(result.get('http_status') == 200, 'seed_identity_not_found')
            _identity(result.get('identity'), t['identity_name'], context(config), t['identity_id'])
            rows.append({'source_id': t['source_id'], 'method': 'inspect_identity', 'result': result})
        else:
            identity = native.get_identity(t['identity_id'])
            _identity(identity, t['identity_name'], context(config), t['identity_id'])
            registration = native.get_registration(t['registration_id'])
            _registration(registration, t['identity_id'], context(config), t['registration_id'])
            rows.extend([{'source_id': t['source_id'], 'method': 'get_identity', 'result': identity},
                         {'source_id': t['source_id'], 'method': 'get_registration', 'result': registration}])
    return {'status': 'a365_readonly_preflight_complete', 'graph_writes': 0, 'results': rows}


def make_plan(config, inventory, preflight, artifact_hashes, binding_path, binding_hash, *, now=None):
    now = now or datetime.now(timezone.utc)
    targets = build_targets(inventory, preflight, context(config))
    # Full source UUID prevents the legacy shortened-key collision; native limit is 48 characters.
    for target in targets:
        target['key'] = 'app' + target['source_id'].replace('-', '')
    payload = {'schema': 1, 'config': config, 'inventory': inventory, 'preflight': preflight,
               'targets': targets, 'native_artifacts': artifact_hashes,
               'binding_input': str(Path(binding_path).resolve()), 'binding_sha256': binding_hash,
               'created_at_utc': now.isoformat(), 'expires_at_utc': (now + timedelta(hours=1)).isoformat(),
               'maximum_identity_creates': sum(t['identity_id'] is None for t in targets),
               'maximum_registration_posts': sum(t['registration_id'] is None for t in targets),
               'identity_sponsor_and_registration_owner': config['operator_id'],
               'scope_note': 'Metadata registration only. No invocation, deployment, rename, delete or grants.'}
    return {**payload, 'scope_sha256': digest(payload)}


def validate_plan(plan, approved_hash, artifacts, *, now=None):
    need(isinstance(plan, dict) and isinstance(plan.get('scope_sha256'), str), 'plan_shape')
    payload = {k: v for k, v in plan.items() if k != 'scope_sha256'}
    need(digest(payload) == plan['scope_sha256'] == approved_hash, 'exact_plan_approval_required')
    config = validate_config(plan['config'])
    validate_inventory(plan['inventory'], config)
    created, expires = timestamp(plan['created_at_utc']), timestamp(plan['expires_at_utc'])
    now = now or datetime.now(timezone.utc)
    need(created <= now <= expires and timedelta(0) < expires - created <= timedelta(hours=1), 'plan_expired_or_future')
    need(plan['native_artifacts'] == artifacts, 'native_build_changed: run preflight again')
    rebuilt = make_plan(config, plan['inventory'], plan['preflight'], artifacts,
                        plan['binding_input'], plan['binding_sha256'], now=created)
    need(rebuilt == plan, 'plan_target_or_operation_drift')
    return plan


def auth_scope(config, inventory, targets):
    return digest({'config': config, 'inventory': inventory, 'targets': targets, 'writes': False})


def authorize_action(action, plan, run_id):
    t = next((t for t in plan['targets'] if t['key'] == action.get('target_key')), None)
    if t is None or action.get('operation') not in ('create_identity', 'register_agent'):
        return False
    expected = {'operation': action['operation'], 'scope_sha256': plan['scope_sha256'],
                'run_id': run_id, **context(plan['config']), 'target_key': t['key'],
                'source_id': t['source_id'], 'identity_name': t['identity_name']}
    if action['operation'] == 'create_identity':
        return t['identity_id'] is None and action == expected
    iid = action.get('identity_id')
    expected.update(identity_id=iid, registration_name=t['registration_name'])
    return t['registration_id'] is None and guid(iid) and (t['identity_id'] is None or iid == t['identity_id']) and action == expected


def print_json(value):
    print(json.dumps(value, indent=2, ensure_ascii=True))


def live_config(config):
    need(not config['operator_upn'].lower().endswith('.invalid') and '.invalid' not in config['dify_origin'],
         'synthetic_example_is_offline_only: configure your own lab')


def inputs(args):
    config = validate_config(read_json(args.config))
    inventory = validate_inventory(read_json(args.inventory), config)
    binding_path = Path(args.bindings).resolve() if args.bindings else STATE / 'bindings.json'
    bindings = read_json(binding_path) if binding_path.exists() else binding_cache(config, {})
    return config, inventory, binding_path, bindings


def fresh_run(kind):
    need(not STATE.is_symlink(), 'state_directory_symlink')
    run_id = str(uuid4())
    directory = STATE / (kind + '-' + run_id)
    directory.mkdir(parents=True, exist_ok=False)
    return run_id, directory


def preflight_command(args, *, verify=False):
    need(args.approve_read, 'read_authentication_requires_approve_read')
    config, inventory, binding_path, bindings = inputs(args)
    live_config(config)
    if not verify:
        age = datetime.now(timezone.utc) - timestamp(inventory['observed_at_utc'])
        need(timedelta(0) <= age <= timedelta(hours=24), 'inventory_stale: collect a fresh checkpoint')
        need(not (STATE / 'apply-pending.json').exists(), 'prior_apply_unresolved: read troubleshooting')
    targets = seeds_for(config, inventory, bindings)
    if verify:
        need(all(t['identity_id'] and t['registration_id'] for t in targets), 'verify_requires_complete_bindings')
    run_id, directory = fresh_run('verify' if verify else 'preflight')
    hashes = runtime_hashes()
    native = NativeClient(config, targets, directory / 'native', auth_scope(config, inventory, targets),
                          run_id, approved=True)
    try:
        preflight = readonly_preflight(native, targets, config)
    finally:
        exit_code = native.close()
    need(exit_code == 0, 'native_session_not_clean')
    need(runtime_hashes() == hashes, 'native_build_changed')
    save_json(directory / 'preflight.json', preflight)
    if verify:
        result = {'status': 'bindings_verified', 'bindings': len(targets), 'identity_reads': len(targets),
                  'registration_reads': len(targets), 'graph_writes': 0, 'fresh_source_scan': False}
        save_json(directory / 'verification.json', result)
        print_json(result)
    else:
        plan = make_plan(config, inventory, preflight, hashes, binding_path,
                         file_sha(binding_path) if binding_path.exists() else None)
        path = directory / 'plan.json'
        save_json(path, plan)
        print_json({'status': 'review_required', 'plan': str(path), 'scope_sha256': plan['scope_sha256'],
                    'maximum_identity_creates': plan['maximum_identity_creates'],
                    'maximum_registration_posts': plan['maximum_registration_posts'],
                    'source_apps': len(plan['targets']), 'graph_writes': 0,
                    'review': 'Read the full plan. Apply only after approving its tenant, client, operator, blueprint, names and counts.'})


def apply_command(args):
    need(args.ack_stop_on_consent, 'stop_on_consent_ack_required')
    plan = validate_plan(read_json(args.plan), args.approve_sha256, runtime_hashes())
    config = plan['config']
    live_config(config)
    binding_path = Path(plan['binding_input'])
    need((file_sha(binding_path) if binding_path.exists() else None) == plan['binding_sha256'], 'binding_cache_changed: repeat preflight')
    need(not (STATE / 'apply-pending.json').exists(), 'prior_apply_unresolved: read troubleshooting')
    need(not (STATE / ('consumed-' + plan['scope_sha256'] + '.json')).exists(), 'plan_already_consumed: do not replay')
    run_id, directory = fresh_run('apply')
    marker = STATE / 'apply-pending.json'
    # Acquired before any authentication. Never automatically removed on error.
    save_json(marker, {'run_id': run_id, 'scope_sha256': plan['scope_sha256'], 'directory': str(directory),
                       'at_utc': utc_text(), 'status': 'incomplete_until_readbacks_verified'})
    consumed = STATE / ('consumed-' + plan['scope_sha256'] + '.json')
    save_json(consumed, {'run_id': run_id, 'scope_sha256': plan['scope_sha256']})
    save_json(directory / 'approved-plan.json', plan)
    events = []

    def event(value):
        save_json(directory / f'event-{len(events) + 1:03d}.json', value)
        events.append(value)

    native = NativeClient(config, plan['targets'], directory / 'native', plan['scope_sha256'], run_id,
                          writes=True, reference='approved-' + plan['scope_sha256'][:16], approved=True)
    try:
        need(native.context() == {k: config[k] for k in ('tenant_id', 'blueprint_id', 'client_id', 'operator_id')}, 'apply_native_context')
        state = {}
        result = reconcile(plan['targets'], native, context(config), plan['scope_sha256'], run_id,
                           state, lambda action: authorize_action(action, plan, run_id), event)
    finally:
        exit_code = native.close()
    need(exit_code == 0, 'native_session_not_clean: inspect pending marker')
    result.update(status='registration_readbacks_verified', run_id=run_id, graph_writes=
                  result['identity_creates'] + result['registration_attempts'], runtime_enabled=False)
    save_json(directory / 'result.json', result)
    replace_json(STATE / 'bindings.json', binding_cache(config, state))
    marker.rename(directory / 'completed-marker.json')
    print_json(result)


def parser():
    p = argparse.ArgumentParser(description='Dify console-API discovery to native A365 registration: explicit lab commands only.')
    sub = p.add_subparsers(dest='command', required=True)
    b = sub.add_parser('build', help='Build .NET helper from an already-installed pinned public CLI; no cloud calls.')
    b.add_argument('--cli-package', help='Optional absolute tools/net8.0/any directory from public CLI 1.1.221.')
    for name in ('check', 'preflight', 'verify'):
        cmd = sub.add_parser(name, help={'check': 'Offline config/inventory/binding checks.',
                                        'preflight': 'Delegated sign-in and Graph reads, then write a local review plan.',
                                        'verify': 'Delegated sign-in and exact existing-binding readbacks, zero Graph writes.'}[name])
        cmd.add_argument('--config', required=True)
        cmd.add_argument('--inventory', required=True)
        cmd.add_argument('--bindings', help='Optional explicit local binding seed JSON; otherwise .local/bindings.json.')
        if name != 'check':
            cmd.add_argument('--approve-read', action='store_true', help='Approve scoped sign-in and reads; stop on consent.')
    apply = sub.add_parser('apply', help='One attempt at the exact hash-approved registration plan; cloud writes.')
    apply.add_argument('--plan', required=True)
    apply.add_argument('--approve-sha256', required=True)
    apply.add_argument('--ack-stop-on-consent', action='store_true')
    return p


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        if args.command == 'build':
            hashes = build(args.cli_package)
            print_json({'status': 'native_built', 'runtime_files': len(hashes), 'cloud_calls': 0})
        elif args.command == 'check':
            config, inventory, _, bindings = inputs(args)
            targets = seeds_for(config, inventory, bindings)
            print_json({'status': 'local_inputs_valid', 'source_apps': len(targets),
                        'workspace_id': config['workspace_id'], 'cloud_calls': 0,
                        'notice': 'No authentication, Graph preflight or live-readiness claim.'})
        elif args.command in ('preflight', 'verify'):
            preflight_command(args, verify=args.command == 'verify')
        else:
            apply_command(args)
        return 0
    except (LabError, OSError, KeyError, TypeError, ValueError) as ex:
        reason = str(ex) if isinstance(ex, LabError) else type(ex).__name__
        print_json({'status': 'stopped', 'reason': reason, 'automatic_retry': False,
                    'recovery': 'If apply started, preserve .local and inspect the pending marker and journals before any new attempt.'})
        return 2
