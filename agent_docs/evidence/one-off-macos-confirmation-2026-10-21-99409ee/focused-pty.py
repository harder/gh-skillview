import sys,os,json,pathlib,tempfile,subprocess,pty,fcntl,termios,struct,signal,time,select,codecs,re,unicodedata,hashlib,shutil
repo=pathlib.Path('/Users/kharder/source/rc-tests/gh-skillview-macos-confirmation')
sys.path.insert(0,str(repo/'tools'))
import test_once as kit
case=sys.argv[1]
out=pathlib.Path('/Users/kharder/source/rc-tests/macos-confirmation-99409ee')
app='/Users/kharder/source/rc-tests/acceptance-e134952-macos/supplemental/app/skillview'
root=pathlib.Path(tempfile.mkdtemp(prefix='skillview-focused-'+case+'-')).resolve()
env=kit.isolated_environment(os.environ,root)
env.update(TERM='xterm-256color',COLORTERM='truecolor',COLUMNS='80',LINES='24',SKILLVIEW_LOG='debug')
env['CLAUDE_CODE_PLUGIN_CACHE_DIR']=str(root/'plugin-cache')
env['CLAUDE_CODE_PLUGIN_DIRS']=''
env['CLAUDE_CODE_PLUGIN_SEED_DIR']=''
home=pathlib.Path(env['HOME'])
if case=='populated':
    skill=home/'.agents/skills/focus-fixture'
    skill.mkdir(parents=True)
    (skill/'SKILL.md').write_text('---\nname: focus-fixture\ndescription: Private startup focus fixture\n---\nNo actions.\n')
if True:
    master,slave=pty.openpty()
    fcntl.ioctl(slave,termios.TIOCSWINSZ,struct.pack('HHHH',24,80,0,0))
    def child():
        os.setsid()
        fcntl.ioctl(0,termios.TIOCSCTTY,0)
    proc=subprocess.Popen([app],stdin=slave,stdout=slave,stderr=slave,env=env,cwd=root,preexec_fn=child)
    os.close(slave)
    screen=[[' ']*80 for _ in range(24)]
    x=y=0
    pending=''
    transcript=bytearray()
    decoder=codecs.getincrementaldecoder('utf-8')('replace')
    def send(value):
        os.write(master, value if isinstance(value, bytes) else value.encode())

    def render(text):
        global x, y, pending
        pending += text
        while pending:
            if pending[0] == "\x1b":
                if len(pending) < 2:
                    return
                if pending[1] == "[":
                    m = re.match(r"\x1b\[([0-?]*)([ -/]*)([@-~])", pending)
                    if not m:
                        return
                    data, inter, cmd = m.groups()
                    pending = pending[m.end() :]
                    nums = [
                        int(v) if v.isdigit() else 0
                        for v in data.lstrip("?=>").split(";")
                    ]
                    n = nums[0] or 1
                    if cmd in "Hf":
                        y = min(23, max(0, n - 1))
                        x = min(79, max(0, (nums[1] if len(nums) > 1 else 1) - 1))
                    elif cmd == "A":
                        y = max(0, y - n)
                    elif cmd == "B":
                        y = min(23, y + n)
                    elif cmd == "C":
                        x = min(79, x + n)
                    elif cmd == "D":
                        x = max(0, x - n)
                    elif cmd == "G":
                        x = min(79, n - 1)
                    elif cmd == "J" and nums[0] in [2, 3]:
                        for row in screen:
                            row[:] = [" "] * 80
                    elif cmd == "K":
                        if nums[0] == 2:
                            screen[y][:] = [" "] * 80
                        else:
                            screen[y][x:] = [" "] * (80 - x)
                    elif cmd == "n" and nums[0] == 6:
                        send("\x1b[1;1R")
                    elif cmd == "t" and nums[0] == 18:
                        send("\x1b[8;24;80t")
                    elif cmd == "c":
                        send("\x1b[?1;2c")
                    elif cmd == "u" and data == "?":
                        send("\x1b[?0u")
                    continue
                if pending[1] == "]":
                    end = pending.find("\x07")
                    end2 = pending.find("\x1b\\")
                    if end < 0 and end2 < 0:
                        return
                    if end >= 0 and (end2 < 0 or end < end2):
                        pending = pending[end + 1 :]
                    else:
                        pending = pending[end2 + 2 :]
                    continue
                pending = pending[2:]
                continue
            ch, pending = pending[0], pending[1:]
            if ch == "\r":
                x = 0
            elif ch == "\n":
                y = min(23, y + 1)
            elif ch == "\b":
                x = max(0, x - 1)
            elif ord(ch) >= 32:
                if unicodedata.combining(ch):
                    continue
                screen[y][min(x, 79)] = ch
                x += 2 if unicodedata.east_asian_width(ch) in "WF" else 1
                if x >= 80:
                    x = 0
                    y = min(23, y + 1)

    def read(seconds=0.1):
        if select.select([master], [], [], seconds)[0]:
            try:
                data = os.read(master, 65536)
            except OSError:
                return
            transcript.extend(data)
            render(decoder.decode(data))

    def text():
        return "\n".join("".join(row) for row in screen)

    def wait(predicate, timeout=60):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            read()
            if predicate():
                return
            if proc.poll() is not None:
                # Exit can race the first predicate check above. poll caches
                # the return code, so recheck before treating it as unexpected.
                assert predicate(), "TUI exited: " + str(proc.returncode) + "\n" + text()
                return
        raise AssertionError("TUI condition timed out\n" + text())

    def click(label, offset=1):
        rows = ["".join(row) for row in screen]
        found = [(row.index(label), i) for i, row in enumerate(rows) if label in row]
        assert found, "Missing control " + label + "\n" + text()
        # Status/instruction text can repeat an action label. Prefer the actual
        # Terminal.Gui button rather than the last textual occurrence.
        buttons = [(xx, yy) for xx, yy in found
                   if rows[yy].rfind("⟦", 0, xx) > rows[yy].rfind("⟧", 0, xx)]
        xx, yy = (buttons or found)[-1]
        xx += offset
        send(f"\x1b[<0;{xx + 1};{yy + 1}M\x1b[<0;{xx + 1};{yy + 1}m")
        for _ in range(4):
            read(0.05)


    started=time.monotonic()
    result={'case':case,'fixture':str(root),'binary':app,'binarySha256':hashlib.sha256(pathlib.Path(app).read_bytes()).hexdigest(),'size':'80x24','terminal':'POSIX PTY xterm-256color; inherited repository probe/ANSI helper','actions':[],'status':'failed'}
    def capture(label):
        (out/(case+'-'+label+'.txt')).write_text(text())
        result['actions'].append({'phase':label,'seconds':round(time.monotonic()-started,3),'transcriptBytes':len(transcript),'activeView':'Installed' if 'Installed skills' in text() else 'Discover' if 'Discover skills' in text() else 'unknown'})
    def logs():
        return '\n'.join(f.read_text() for f in home.rglob('skillview-*.log'))
    def settle():
        deadline=time.monotonic()+2
        while time.monotonic()<deadline:read(.1)
    try:
        count='1' if case=='populated' else '0'
        wait(lambda: 'filesystem scan found '+count+' skill(s)' in logs() and 'gh skill list returned' in logs() and 'gh 2.102.0 — press' in text(),60)
        if case=='populated':wait(lambda:'Installed skills' in text(),30)
        settle()
        capture('settled-before-input')
        assert ('Installed skills' if case=='populated' else 'Discover skills') in text(), 'Unexpected settled view'
        result['startupInventorySettled']=True
        if case=='populated':
            send('/')
            wait(lambda:'Discover skills' in text() and 'Search:' in text(),30)
            settle()
            capture('slash-before-typing')
        send('focusprobe')
        wait(lambda:'Search: focusprobe' in text() and 'Discover skills' in text(),30)
        settle()
        capture('after-typing')
        result['entireQueryInDiscover']=True
        send(b'\x11')
        wait(lambda:proc.poll() is not None,20)
        result['exitCode']=proc.returncode
        assert proc.returncode==0,'Ctrl+Q did not exit cleanly'
        result['status']='passed'
    except Exception as error:
        result['error']=type(error).__name__+': '+str(error)
        capture('failure')
    finally:
        if proc.poll() is None:
            os.killpg(proc.pid,signal.SIGKILL)
        os.close(master)
        proc.wait(timeout=10)
        result['processStopped']=proc.poll() is not None
        (out/(case+'.transcript')).write_bytes(transcript)
        for f in home.rglob('skillview-*.log'):shutil.copyfile(f,out/(case+'-app.log'))
        (out/(case+'.json')).write_text(json.dumps(result,indent=2)+'\n')
        print(json.dumps(result,indent=2))
    sys.exit(0 if result['status']=='passed' else 1)
