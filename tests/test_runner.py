import copy
import tempfile
import unittest
from argparse import Namespace
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

from dify_a365 import runner
from dify_a365.contract import LabError, context, digest, file_sha, read_json, save_json
from test_lab import CTX, Native, config, fixtures

HASHES = {'WrapperNative.dll': 'a' * 64}
NOW = datetime(2026, 9, 29, 1, tzinfo=timezone.utc)


class FakeNewNative:
    instances = []

    def __init__(self, lab, targets, directory, scope, run_id, **kwargs):
        self.lab = lab
        self.targets = copy.deepcopy(targets)
        self.posts = []
        self.instances.append(self)

    def context(self):
        return {k: self.lab[k] for k in ('tenant_id', 'blueprint_id', 'client_id', 'operator_id')}

    def find_identity(self, name):
        return {'complete': True, 'items': []}

    def create_identity(self, action):
        t = next(t for t in self.targets if t['key'] == action['target_key'])
        t['identity_id'] = '70000000' + t['source_id'][8:]
        self.posts.append(action)
        return {'id': t['identity_id'], 'http_status': 201}

    def get_identity(self, iid):
        t = next(t for t in self.targets if t['identity_id'] == iid)
        return {'id': iid, 'appId': iid, 'displayName': t['identity_name'],
                'accountEnabled': True, 'agentIdentityBlueprintId': self.lab['blueprint_id']}

    def register(self, action):
        t = next(t for t in self.targets if t['key'] == action['target_key'])
        t['registration_id'] = 'T_' + t['source_id']
        self.posts.append(action)
        return {'id': t['registration_id'], 'http_status': 201, 'created': True,
                'native_method': 'GraphApiService.RegisterAgentInstanceAsyncV2'}

    def get_registration(self, rid):
        t = next(t for t in self.targets if t['registration_id'] == rid)
        return {'id': rid, 'agentIdentityId': t['identity_id'], 'sourceAgentId': t['identity_id'],
                'agentIdentityBlueprintId': self.lab['blueprint_id']}

    def close(self):
        return 0


class RunnerTests(unittest.TestCase):
    def test_plan_hash_expiry_build_and_target_drift(self):
        d, p = fixtures()
        plan = runner.make_plan(config(), d, p, HASHES, 'nonexistent-binding.json', None, now=NOW)
        self.assertEqual((plan['maximum_identity_creates'], plan['maximum_registration_posts']), (1, 2))
        self.assertEqual(runner.validate_plan(plan, plan['scope_sha256'], HASHES, now=NOW), plan)
        self.assertEqual(len(plan['targets'][0]['key']), 35)
        for approved, hashes, time in [('b' * 64, HASHES, NOW), (plan['scope_sha256'], {}, NOW),
                                      (plan['scope_sha256'], HASHES, NOW + timedelta(hours=2))]:
            with self.assertRaises(LabError):
                runner.validate_plan(plan, approved, hashes, now=time)
        changed = copy.deepcopy(plan)
        changed['targets'][0]['identity_name'] = 'tampered'
        changed['scope_sha256'] = digest({k: v for k, v in changed.items() if k != 'scope_sha256'})
        with self.assertRaisesRegex(LabError, 'plan_target_or_operation_drift'):
            runner.validate_plan(changed, changed['scope_sha256'], HASHES, now=NOW)

    def test_authorizer_rejects_cross_scope_source_and_extra_fields(self):
        d, p = fixtures()
        plan = runner.make_plan(config(), d, p, HASHES, 'bindings.json', None, now=NOW)
        t = plan['targets'][-1]
        action = {'operation': 'create_identity', 'scope_sha256': plan['scope_sha256'], 'run_id': 'test-run',
                  **CTX, 'target_key': t['key'], 'source_id': t['source_id'], 'identity_name': t['identity_name']}
        self.assertTrue(runner.authorize_action(action, plan, 'test-run'))
        for key, value in [('scope_sha256', 'wrong'), ('source_id', 'wrong'), ('tenant_id', 'wrong'),
                           ('unexpected', 'value'), ('operation', 'delete')]:
            self.assertFalse(runner.authorize_action({**action, key: value}, plan, 'test-run'))

    def test_bindings_cannot_cross_instance_tenant_or_duplicate_identity(self):
        d, _ = fixtures()
        empty = runner.binding_cache(config(), {})
        self.assertEqual(len(runner.seeds_for(config(), d, empty)), 5)
        for key in ('dify_origin', 'tenant_id', 'workspace_id', 'blueprint_id'):
            altered = copy.deepcopy(empty)
            altered['source'][key] = 'wrong'
            with self.assertRaises(LabError):
                runner.seeds_for(config(), d, altered)
        duplicate = copy.deepcopy(empty)
        duplicate['items'] = {a['id']: {'identity_id': '70000000-0000-4000-8000-000000000001',
                             'identity_name': a['name']} for a in d['apps'][:2]}
        with self.assertRaises(LabError):
            runner.seeds_for(config(), d, duplicate)

    def test_five_new_apps_apply_then_readonly_verification(self):
        d, _ = fixtures()
        c = config()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            targets = runner.seeds_for(c, d, runner.binding_cache(c, {}))
            fake = FakeNewNative(c, targets, root, 'a' * 64, 'offline')
            preflight = runner.readonly_preflight(fake, targets, c)
            self.assertEqual(fake.posts, [])
            initial = root / 'initial-seeds.json'
            save_json(initial, runner.binding_cache(c, {}))
            plan = runner.make_plan(c, d, preflight, HASHES, initial, file_sha(initial))
            plan_path = root / 'plan.json'
            save_json(plan_path, plan)
            args = Namespace(plan=str(plan_path), approve_sha256=plan['scope_sha256'], ack_stop_on_consent=True)
            with patch.object(runner, 'STATE', root), patch.object(runner, 'NativeClient', FakeNewNative), \
                 patch.object(runner, 'runtime_hashes', return_value=HASHES), \
                 patch.object(runner, 'live_config'), patch.object(runner, 'print_json'):
                runner.apply_command(args)
                self.assertEqual(len(FakeNewNative.instances[-1].posts), 10)
                self.assertFalse((root / 'apply-pending.json').exists())
                saved = read_json(root / 'bindings.json')
                verified_targets = runner.seeds_for(c, d, saved)
                readonly = FakeNewNative(c, verified_targets, root, 'b' * 64, 'verify')
                result = runner.readonly_preflight(readonly, verified_targets, c)
                self.assertEqual(result['graph_writes'], 0)
                self.assertEqual(readonly.posts, [])
                self.assertEqual(len(result['results']), 10)
                with self.assertRaises(LabError):
                    runner.apply_command(args)
                self.assertFalse((root / 'apply-pending.json').exists())

    def test_uncertain_apply_leaves_marker_and_never_retries(self):
        d, _ = fixtures()
        c = config()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            targets = runner.seeds_for(c, d, runner.binding_cache(c, {}))
            preflight = runner.readonly_preflight(FakeNewNative(c, targets, root, '', ''), targets, c)
            plan = runner.make_plan(c, d, preflight, HASHES, root / 'bindings.json', None)
            save_json(root / 'plan.json', plan)
            args = Namespace(plan=str(root / 'plan.json'), approve_sha256=plan['scope_sha256'], ack_stop_on_consent=True)
            with patch.object(runner, 'STATE', root), patch.object(runner, 'NativeClient', FakeNewNative), \
                 patch.object(runner, 'runtime_hashes', return_value=HASHES), patch.object(runner, 'live_config'), \
                 patch.object(FakeNewNative, 'register', side_effect=LabError('registration_outcome_unknown')) as register:
                with self.assertRaisesRegex(LabError, 'registration_outcome_unknown'):
                    runner.apply_command(args)
                self.assertTrue((root / 'apply-pending.json').exists())
                self.assertEqual(register.call_count, 1)
                with self.assertRaises(LabError):
                    runner.apply_command(args)
                self.assertEqual(register.call_count, 1)

    def test_example_configuration_never_enters_live_mode(self):
        with self.assertRaisesRegex(LabError, 'synthetic_example_is_offline_only'):
            runner.live_config(config())


if __name__ == '__main__':
    unittest.main()
