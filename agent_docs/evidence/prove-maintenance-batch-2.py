import hashlib,json,os,stat,subprocess,tempfile
from pathlib import Path
import argparse
parser=argparse.ArgumentParser(description='Disposable native CLI proof for maintenance batch 2')
parser.add_argument('--app',required=True,type=Path)
parser.add_argument('--gh',required=True,action='append',type=Path)
parser.add_argument('--output',required=True,type=Path)
args=parser.parse_args()
base=args.output.parent
current=args.gh[-1]
auth=subprocess.run([str(current),'auth','token'],capture_output=True,text=True,check=True).stdout.strip()
results=[]
def fingerprint(path):
 entries=[]
 for p in sorted(path.rglob('*')):
  info=p.lstat();content=os.readlink(p).encode() if p.is_symlink() else p.read_bytes() if p.is_file() else b''
  entries.append((str(p.relative_to(path)),stat.S_IFMT(info.st_mode),stat.S_IMODE(info.st_mode),hashlib.sha256(content).hexdigest()))
 return hashlib.sha256(json.dumps(entries).encode()).hexdigest()
for gh in args.gh:
 with tempfile.TemporaryDirectory(prefix='skillview-batch2-proof-') as temp:
  root=Path(temp);home=root/'home';workspace=root/'workspace';home.mkdir();workspace.mkdir()
  roots=[root/'first',root/'second']
  env=os.environ.copy();env.update(HOME=str(home),USERPROFILE=str(home),GH_TOKEN=auth,GH_PATH=str(gh),GH_EXTENSION='1',GH_CONFIG_DIR=str(root/'gh-config'),XDG_CACHE_HOME=str(root/'cache'),XDG_STATE_HOME=str(root/'state'),XDG_CONFIG_HOME=str(root/'config'),XDG_DATA_HOME=str(root/'data'),APPDATA=str(root/'appdata'),LOCALAPPDATA=str(root/'localappdata'),GH_NO_UPDATE_NOTIFIER='1',GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
  for directory in roots:
   r=subprocess.run([str(gh),'skill','install','--dir',str(directory),'--pin=33789149b9def6b66ddf247438a79fac94e22074','--','cli/cli','skills/gh-skill'],env=env,cwd=workspace,capture_output=True,text=True,timeout=120)
   assert r.returncode==0,r.stderr
  def run(command_args,json_output=True):
   command=[str(args.app)]
   for directory in roots:command.extend(['--scan-root',str(directory)])
   r=subprocess.run(command+command_args,env=env,cwd=workspace,capture_output=True,text=True,timeout=180)
   return r,json.loads(r.stdout) if json_output else None
  r,catalog=run(['list','--json']);assert r.returncode==0,r.stderr
  rows=catalog['skills'];assert len(rows)==2,rows
  assert all(Path(s['resolvedPath']).resolve().is_relative_to(root.resolve()) for s in rows)
  first=next(s for s in rows if Path(s['resolvedPath']).is_relative_to(roots[0]));second=next(s for s in rows if s['installationId']!=first['installationId'])
  paths=[Path(first['resolvedPath']),Path(second['resolvedPath'])]
  original=[fingerprint(p) for p in paths]
  r,_=run(['update',first['name'],'--unpin'],False);assert r.returncode!=0 and 'multiple installations' in r.stderr,r.stderr
  assert original==[fingerprint(p) for p in paths]
  r,updated=run(['update','--install-id',first['installationId'],'--unpin','--json']);assert r.returncode==0,updated
  assert updated['schemaVersion']==2 and updated['mutationAttempted'] and len(updated['outcomes'])==1
  assert updated['outcomes'][0]['status']=='changed' and updated['outcomes'][0]['beforeFingerprint']!=updated['outcomes'][0]['afterFingerprint']
  assert fingerprint(paths[0])!=original[0] and fingerprint(paths[1])==original[1]
  paths[1].joinpath('custom.txt').write_text('protect this local addition')
  protected=fingerprint(paths[1])
  lock=home/'.agents/.skill-lock.json';lock_before=lock.read_bytes() if lock.exists() else None
  r,refused=run(['update','--path',str(paths[1]),'--unpin','--force','--json']);assert r.returncode!=0,refused
  assert not refused['mutationAttempted'] and refused['partial'] and refused['outcomes'][0]['status']=='refused'
  assert 'Local changes protected' in refused['outcomes'][0]['message'] and fingerprint(paths[1])==protected
  assert (lock.read_bytes() if lock.exists() else None)==lock_before,'Comparison staging changed the real shared lockfile'
  version=subprocess.check_output([str(gh),'--version'],text=True).splitlines()[0]
  results.append(dict(gh=version,nativeAotHost=True,ambiguousNameRefused=True,exactIdChangedOneCopy=True,unselectedCopyUnchanged=True,forcePreservedLocalExtra=True,refusalPreservedSharedLock=True,jsonSchemaVersion=2,retainedStdout=bool(updated['stdout']),retainedStderr=bool(updated['stderr']),refusalMutationAttempted=False))
args.output.write_text(json.dumps(results,indent=2));print(json.dumps(results,indent=2))
