import copy
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock

from dify_a365.contract import LabError, digest, read_json, save_json, validate_config, validate_inventory
from dify_a365.platform_registration import build_targets, reconcile

CTX = {k: f'{i}0000000-0000-4000-8000-000000000001' for i, k in enumerate(
    ('tenant_id', 'blueprint_id', 'client_id', 'operator_id', 'workspace_id'), 1)}


def config():
    return {'schema': 1, **CTX, 'operator_upn': 'operator@example.invalid', 'dify_origin': 'https://dify.example.invalid'}


def fixtures():
    apps = [{'id': f'60000000-0000-4000-8000-{i:012d}', 'name': f'Dify app {i}',
             'mode': 'advanced-chat' if i == 5 else 'chat'} for i in range(1, 6)]
    rows = []
    for i, a in enumerate(apps, 1):
        iid = f'70000000-0000-4000-8000-{i:012d}'
        identity = {'id': iid, 'appId': iid, 'displayName': f'Existing {i}',
                    'accountEnabled': True, 'agentIdentityBlueprintId': CTX['blueprint_id']}
        if i <= 3:
            rows.extend([{'source_id': a['id'], 'method': 'get_identity', 'result': identity},
                         {'source_id': a['id'], 'method': 'get_registration', 'result': {
                             'id': f'T_saved-{i}', 'agentIdentityId': iid, 'sourceAgentId': iid,
                             'agentIdentityBlueprintId': CTX['blueprint_id']}}])
        elif i == 4:
            rows.append({'source_id': a['id'], 'method': 'inspect_identity',
                         'result': {'http_status': 200, 'identity': identity}})
        else:
            rows.append({'source_id': a['id'], 'method': 'find_identity', 'name': a['name'],
                         'result': {'complete': True, 'items': []}})
    discovery = {'schema': 1, 'complete': True, 'total': 5, 'workspace_id': CTX['workspace_id'],
                 'instance_url': config()['dify_origin'],
                 'observed_at_utc': '2026-09-29T00:00:00Z', 'credentials_returned': False,
                 'selection_filters': {'mode': 'all', 'name': None, 'source_ids': None, 'created_by_me': False},
                 'apps': apps, 'pages': [{'page': 1, 'limit': 100, 'total': 5, 'count': 5, 'has_more': False}]}
    return discovery, {'status': 'a365_readonly_preflight_complete', 'graph_writes': 0, 'results': rows}


class Native:
    def __init__(self, targets):
        self.targets = targets
        self.created = self.posts = 0
        self.registration_actions = []

    def find_identity(self, name):
        return {'complete': True, 'items': []}

    def create_identity(self, action):
        self.created += 1
        return {'id': '70000000-0000-4000-8000-000000000005', 'http_status': 201}

    def get_identity(self, iid):
        t = next(t for t in self.targets if t['identity_id'] == iid or t['identity_id'] is None)
        return {'id': iid, 'appId': iid, 'displayName': t['identity_name'],
                'accountEnabled': True, 'agentIdentityBlueprintId': CTX['blueprint_id']}

    def register(self, action):
        self.posts += 1
        self.registration_actions.append(action)
        return {'id': 'T_created-' + str(self.posts), 'created': True, 'http_status': 201,
                'native_method': 'GraphApiService.RegisterAgentInstanceAsyncV2'}

    def get_registration(self, rid):
        if rid.startswith('T_saved-'):
            iid = f'70000000-0000-4000-8000-{int(rid[-1]):012d}'
        else:
            iid = self.registration_actions[int(rid[-1]) - 1]['identity_id']
        return {'id': rid, 'agentIdentityId': iid, 'sourceAgentId': iid,
                'agentIdentityBlueprintId': CTX['blueprint_id']}


class LabTests(unittest.TestCase):
    def test_five_item_original_scenario_and_zero_write_repeat(self):
        d, p = fixtures()
        targets = build_targets(d, p, CTX)
        native = Native(targets)
        state, actions = {}, []
        authorize = lambda a: actions.append(a) is None
        first = reconcile(targets, native, CTX, 'a' * 64, '80000000-0000-4000-8000-000000000001', state, authorize)
        second = reconcile(targets, native, CTX, 'a' * 64, '80000000-0000-4000-8000-000000000002', state, authorize)
        self.assertEqual(first['bindings'], second['bindings'])
        self.assertEqual((first['identity_creates'], first['registration_attempts'],
                          second['identity_creates'], second['registration_attempts']), (1, 2, 0, 0))
        self.assertEqual((native.created, native.posts, len(actions)), (1, 2, 3))
        self.assertEqual(native.registration_actions[0]['registration_name'], 'Dify app 4')
        self.assertEqual(native.registration_actions[0]['identity_name'], 'Existing 4')
        self.assertFalse(first['fresh_source_scan'])

    def test_no_write_approval_means_zero_posts(self):
        d, p = fixtures()
        targets = build_targets(d, p, CTX)
        native = Native(targets)
        with self.assertRaisesRegex(LabError, 'write_approval_required'):
            reconcile(targets, native, CTX, 'a' * 64, 'run', {}, lambda _: False)
        self.assertEqual((native.created, native.posts), (0, 0))

    def test_discovery_rejects_filters_duplicates_incomplete_or_wrong_workspace(self):
        mutations = [lambda d: d.update(complete=False),
                     lambda d: d['selection_filters'].update(source_ids=['pinned']),
                     lambda d: d['apps'].append(copy.deepcopy(d['apps'][0])),
                     lambda d: d.update(workspace_id='wrong')]
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                d, p = fixtures()
                mutate(d)
                with self.assertRaises(LabError):
                    build_targets(d, p, CTX)

    def test_changed_lookup_stops_without_duplicate(self):
        d, p = fixtures()
        targets = build_targets(d, p, CTX)
        native = Native(targets)
        native.find_identity = lambda _: {'complete': True, 'items': [{'id': 'existing'}]}
        with self.assertRaisesRegex(LabError, 'identity_lookup_changed'):
            reconcile(targets, native, CTX, 'a' * 64, 'run', {}, lambda _: True)
        self.assertEqual(native.created, 0)

    def test_wrong_registration_identity_stops(self):
        d, p = fixtures()
        p['results'][1]['result']['agentIdentityId'] = 'other'
        with self.assertRaisesRegex(LabError, 'registration_binding'):
            build_targets(d, p, CTX)

    def test_registry_source_id_is_identity_not_dify_uuid(self):
        d, p = fixtures()
        p['results'][1]['result']['sourceAgentId'] = d['apps'][0]['id']
        with self.assertRaisesRegex(LabError, 'registration_binding'):
            build_targets(d, p, CTX)

    def test_config_is_portable_and_context_bound(self):
        c = validate_config(config())
        self.assertEqual(c, config())
        for key, value in [('client_id', 'not-a-guid'), ('operator_upn', 'bad'),
                           ('dify_origin', 'https://user:password@example.invalid'),
                           ('dify_origin', 'https://example.invalid/console/api'),
                           ('dify_origin', 'http://192.0.2.10')]:
            with self.subTest(key=key, value=value), self.assertRaises(LabError):
                validate_config({**c, key: value})
        with self.assertRaises(LabError):
            validate_config({**c, 'client_secret': 'not-permitted'})

    def test_inventory_validates_instance_pagination_and_bounds(self):
        d, _ = fixtures()
        self.assertEqual(validate_inventory(d, config())['total'], 5)
        for mutate in [lambda x: x.update(instance_url='https://another.example.invalid'),
                       lambda x: x['pages'][0].update(has_more=True),
                       lambda x: x['pages'][0].update(total=6),
                       lambda x: x.update(credentials_returned=True),
                       lambda x: x['apps'][0].update(name='bad\nname')]:
            broken = copy.deepcopy(d)
            mutate(broken)
            with self.subTest(mutate=mutate), self.assertRaises(LabError):
                validate_inventory(broken, config())

    def test_json_duplicate_keys_and_nonfinite_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'input.json'
            for text in ['{"schema":1,"schema":2}', '{"value":NaN}', '{"outer":{"a":1,"a":2}}']:
                path.write_text(text, encoding='utf-8')
                with self.assertRaises(LabError):
                    read_json(path)

    def test_receipts_are_create_only_and_hash_is_canonical(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'new.json'
            save_json(path, {'b': 2, 'a': 1})
            with self.assertRaises(FileExistsError):
                save_json(path, {'different': True})
            self.assertEqual(digest(read_json(path)), digest({'a': 1, 'b': 2}))


if __name__ == '__main__':
    unittest.main()
