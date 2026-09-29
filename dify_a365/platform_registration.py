"""Registration phase of a checkpointed platform-discovery demo.

Consumes the complete Dify console-API metadata checkpoint, not SQL or fixture
selectors. This phase deliberately does not claim to perform a fresh source scan.
No invocation-service startup, catalog mutation, rename, deletion or retry exists.
"""
import re
from .contract import need, guid

def _identity(value, name, context, iid=None):
    need(isinstance(value,dict) and guid(value.get('id')),'identity_readback')
    need((iid is None or value['id']==iid) and value.get('appId')==value['id'] and value.get('displayName')==name and value.get('accountEnabled') is True and value.get('agentIdentityBlueprintId')==context['blueprint_id'],'identity_binding')
    return value['id']

def _registration(value, iid, context, rid=None):
    need(isinstance(value,dict) and isinstance(value.get('id'),str) and re.fullmatch(r'[A-Za-z0-9_-]{1,128}',value['id']),'registration_readback')
    need((rid is None or value['id']==rid) and value.get('agentIdentityId')==iid and value.get('sourceAgentId')==iid and value.get('agentIdentityBlueprintId')==context['blueprint_id'],'registration_binding')
    return value['id']

def build_targets(discovery, preflight, context):
    need(discovery.get('complete') is True and discovery.get('workspace_id')==context['workspace_id'],'discovery_context')
    need(discovery.get('selection_filters')=={'created_by_me':False,'mode':'all','name':None,'source_ids':None},'filtered_discovery')
    apps=discovery.get('apps',[])
    need(isinstance(apps,list) and 1<=len(apps)<=5 and discovery.get('total')==len(apps),'discovery_count')
    need(preflight.get('status')=='a365_readonly_preflight_complete' and preflight.get('graph_writes')==0,'preflight_status')
    ids=[a.get('id') for a in apps]
    need(all(guid(i) for i in ids) and len(set(ids))==len(ids),'discovery_duplicate_ids')
    need(set(r.get('source_id') for r in preflight['results'])==set(ids),'preflight_coverage')
    result=[]
    for app in apps:
        raw=app.get('name');need(isinstance(raw,str) and 1<=len(raw)<=256 and not any(ord(c)<32 or ord(c)==127 for c in raw),'source_name')
        display=raw.strip();need(bool(display),'source_name')
        need(app.get('mode') in ('chat','advanced-chat','agent-chat','workflow','completion'),'source_mode')
        rows=[r for r in preflight['results'] if r['source_id']==app['id']]
        by={r['method']:r for r in rows};need(len(by)==len(rows),'preflight_ambiguous')
        iid=rid=None;name=display
        if 'get_identity' in by:
            need(set(by)=={'get_identity','get_registration'},'preflight_reuse_shape')
            identity=by['get_identity']['result'];name=identity['displayName'];iid=_identity(identity,name,context)
            rid=_registration(by['get_registration']['result'],iid,context)
        elif 'inspect_identity' in by:
            need(set(by)=={'inspect_identity'} and by['inspect_identity']['result']['http_status']==200,'preflight_inspection')
            identity=by['inspect_identity']['result']['identity'];name=identity['displayName'];iid=_identity(identity,name,context)
        else:
            need(set(by)=={'find_identity'} and by['find_identity']['name']==display and by['find_identity']['result']=={'complete':True,'items':[]},'preflight_empty_lookup')
        result.append({'key':'app'+app['id'].replace('-','')[:12],'source_id':app['id'],'source_name':raw,'source_mode':app['mode'],'identity_name':name,'identity_id':iid,'registration_id':rid,'registration_name':display,'name_normalization':'trim_outer_whitespace' if raw!=display else 'none'})
    for field in ('identity_id','registration_id'):
        values=[t[field] for t in result if t[field] is not None];need(len(values)==len(set(values)),'duplicate_mapping')
    need(len({t['identity_name'].casefold() for t in result})==len(result),'duplicate_identity_name')
    return result

def reconcile(targets,native,context,scope,run_id,state,authorize,on_event=lambda e:None):
    counts={'identity_creates':0,'registration_attempts':0,'registrations_created':0};bindings=[]
    for t in targets:
        sid=t['source_id'];previous=state.get(sid,{})
        iid=previous.get('identity_id',t['identity_id']);rid=previous.get('registration_id',t['registration_id'])
        need((t['identity_id'] is None or iid==t['identity_id']) and (t['registration_id'] is None or rid==t['registration_id']),'seed_binding_changed')
        def action(op):
            a={'operation':op,'scope_sha256':scope,'run_id':run_id,**context,'target_key':t['key'],'source_id':sid,'identity_name':t['identity_name']}
            if op=='register_agent':a.update(identity_id=iid,registration_name=t['registration_name'])
            need(authorize(a),'write_approval_required');on_event({'status':'write_intent','action':a});return a
        if iid is None:
            need(native.find_identity(t['identity_name'])=={'complete':True,'items':[]},'identity_lookup_changed')
            value=native.create_identity(action('create_identity'));need(value.get('http_status')==201 and guid(value.get('id')),'identity_create_result')
            iid=value['id'];counts['identity_creates']+=1;on_event({'status':'identity_candidate','source_id':sid,'identity_id':iid})
        _identity(native.get_identity(iid),t['identity_name'],context,iid)
        if rid is None:
            value=native.register(action('register_agent'))
            need(value.get('native_method')=='GraphApiService.RegisterAgentInstanceAsyncV2' and ((value.get('http_status')==201 and value.get('created') is True) or (value.get('http_status')==409 and value.get('created') is False)),'registration_outcome_unverified')
            rid=value.get('id');counts['registration_attempts']+=1;counts['registrations_created']+=int(value['created'])
            on_event({'status':'registration_candidate','source_id':sid,'registration_id':rid,'http_status':value['http_status']})
        _registration(native.get_registration(rid),iid,context,rid)
        need(not any(s!=sid and (b['identity_id']==iid or b['registration_id']==rid) for s,b in state.items()),'duplicate_result_binding')
        binding={'source_id':sid,'source_name':t['source_name'],'source_mode':t['source_mode'],'identity_name':t['identity_name'],'identity_id':iid,'registration_id':rid}
        state[sid]=binding;bindings.append(binding);on_event({'status':'binding_verified','binding':binding})
    return {'bindings':bindings,**counts,'fresh_source_scan':False}
