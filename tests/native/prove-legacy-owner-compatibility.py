"""Supplementary actual pre-change-binary saved-plan restart proof, disposable only."""
import argparse, hashlib, json, os, pathlib, shutil, stat, subprocess, tempfile, time
p=argparse.ArgumentParser();p.add_argument('--legacy',type=pathlib.Path,required=True);p.add_argument('--app',type=pathlib.Path,required=True);p.add_argument('--output',type=pathlib.Path,required=True);p.add_argument('--managed-discovery-fixtures',action='store_true');a=p.parse_args()
legacy=a.legacy.resolve();app=a.app.resolve();gh=pathlib.Path(shutil.which('gh')).resolve()
auth=os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN') or subprocess.check_output([str(gh),'auth','token'],text=True).strip()
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def snapshot(root):
 rows=[]
 for path in [root,*sorted(root.rglob('*'))]:
  s=path.lstat();kind='link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
  data=os.readlink(path).encode() if kind=='link' else path.read_bytes() if kind=='file' else b''
  rows.append(dict(path=str(path.relative_to(root)),kind=kind,device=s.st_dev,inode=s.st_ino,mode=stat.S_IMODE(s.st_mode),sha256=hashlib.sha256(data).hexdigest()))
 return rows
with tempfile.TemporaryDirectory(prefix='skillview-legacy-owner-proof-') as temporary:
 root=pathlib.Path(temporary).resolve();home=root/'home';home.mkdir()
 env={k:v for k,v in os.environ.items() if k not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not k.startswith(('GIT_','SKILLVIEW_'))}
 env.update(HOME=str(home),USERPROFILE=str(home),GH_TOKEN=auth,GH_EXTENSION='1',GH_PATH=str(gh),GH_CONFIG_DIR=str(root/'gh-config'),SKILLVIEW_STATE_HOME=str(root/'state'),XDG_CONFIG_HOME=str(root/'config'),XDG_STATE_HOME=str(root/'xdg-state'),XDG_DATA_HOME=str(root/'data'),XDG_CACHE_HOME=str(root/'cache'),APPDATA=str(root/'appdata'),LOCALAPPDATA=str(root/'localappdata'),CLAUDE_CONFIG_DIR=str(home/'.claude'),PI_CODING_AGENT_DIR=str(home/'.pi/agent'),GIT_CONFIG_COUNT='0',GH_TELEMETRY='false',GH_NO_UPDATE_NOTIFIER='1',GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
 install=subprocess.run([str(gh),'skill','install','--agent','universal','--scope','user','--','cli/cli','skills/gh-skill'],cwd=root,env=env,capture_output=True,text=True,timeout=180);assert install.returncode==0,install.stderr
 payload=home/'.agents/skills/gh-skill';registry=home/'.agents/.skill-lock.json'
 registry_object=json.loads(registry.read_bytes());selector='gh-skill';registry_object['skills'][selector]['updatedAt']='2020-01-01T00:00:00Z';registry.write_text(json.dumps(registry_object,indent=2)+'\n')
 original=snapshot(payload);owner=registry.read_bytes()
 def args(host,command,*arguments):return [str(host),command,*map(str,arguments),'--allow-hidden-dirs','--json']
 def run(host,command,*arguments):
  r=subprocess.run(args(host,command,*arguments),cwd=root,env=env,capture_output=True,text=True,timeout=180)
  assert r.returncode==0,(r.returncode,r.stdout,r.stderr)
  return json.loads(r.stdout)
 plan=run(legacy,'update-plan','--path',payload)['operation'];transition=plan['revision']['owner'];assert transition['beforeFingerprint']!=transition['desiredFingerprint'],transition
 assert plan['revision']['source']['tree']==plan['subject']['metadata']['githubTreeSha']
 plan_sha=sha(pathlib.Path(plan['planPath']))
 if a.managed_discovery_fixtures:
  builtin=home/'.codex/skills/.system/builtin-fixture';builtin.mkdir(parents=True);(builtin/'SKILL.md').write_bytes(b'---\nname: builtin-fixture\ndescription: Compatibility fixture\n---\nBody\n')
  plugin=home/'.codex/plugins/cache/fixture-market/fixture-plugin/1.0.0';meta=plugin/'.codex-plugin';meta.mkdir(parents=True);(meta/'plugin.json').write_text('{"name":"fixture-plugin"}')
  packaged=plugin/'skills/packaged-fixture';packaged.mkdir(parents=True);(packaged/'SKILL.md').write_bytes(b'---\nname: packaged-fixture\ndescription: Compatibility fixture\n---\nBody\n')
  observed=run(app,'list');managed=[item for item in observed['skills'] if item['name'] in ('builtin-fixture','packaged-fixture')]
  assert len(managed)==2 and all(any(owner['readOnly'] for owner in item['ownerRecords']) for item in managed),observed
 inspected=run(app,'update-inspect','--id',plan['id'])['operation'];assert inspected['status']=='ready',inspected
 worker=subprocess.Popen(args(legacy,'update-apply','--id',plan['id'],'--yes','--disposition','retain-original','--expect',plan['planFingerprint']),cwd=root,env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
 try:
  end=time.monotonic()+90;killed=False
  while time.monotonic()<end and worker.poll() is None:
   if pathlib.Path(plan['retainedPath']).exists():worker.kill();killed=True;break
   time.sleep(.002)
  out,err=worker.communicate(timeout=60);assert killed and worker.returncode!=0,(out,err)
 finally:
  if worker.poll() is None:worker.kill();worker.communicate(timeout=60)
 interrupted=run(app,'update-inspect','--id',plan['id'])['operation'];assert interrupted['status'] in ('original-retained','installed'),interrupted
 recovered=run(app,'update-recover','--id',plan['id'],'--yes','--expect',interrupted['reviewFingerprint'])['operation'];assert recovered['succeeded'] and recovered['status']=='installed',recovered
 reviewed=run(app,'update-inspect','--id',plan['id'])['operation'];undone=run(app,'update-undo','--id',plan['id'],'--yes','--expect',reviewed['reviewFingerprint'])['operation'];assert undone['succeeded'] and undone['status']=='restored',undone
 assert snapshot(payload)==original and registry.read_bytes()==owner
 result=dict(newManagedRootsObservedAfterSavingPlan=a.managed_discovery_fixtures,legacyHostSha256=sha(legacy),nativeHostSha256=sha(app),gh=subprocess.check_output([str(gh),'--version'],text=True).splitlines()[0],savedPlanSha256=plan_sha,sameTreeOwnerTransition=True,persistedLegacyPlanInspected=True,actualLegacyApplyKilled=True,interruptedStatus=interrupted['status'],forwardRecoveryVerified=True,undoPreservedOriginalNativeObjectsBytesModes=True,exactOriginalRegistryBytesRestored=True)
 a.output.write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
