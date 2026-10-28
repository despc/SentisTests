r"""What SentisClusters gives a server with a heavy cluster: heavy_neighbour (a heap of welding sites, light spots far
off, a drifting probe in each) run four times - SentisClusters' parallel updates off, on, detaching alone, both - and
compared: each probe's speed of simulation, the server's frames, what was welded.

Usage:  python tools\clusters_compare.py [modes] [scenario]
        modes: off,parallel,detachonly,detach (all four by default); scenario: heavy_neighbour (by default) - one heavy heap and
        light spots far off - or medium_clusters - the same welding in 8 groups, a cluster each

Restarts the stand for each mode (Torch stopped, Instance\SentisClusters.cfg set, Torch started as tools\deploy.ps1
starts it), puts the config back as it was at the end. The stand must not be needed meanwhile: about 5 minutes a mode.
"""
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, r'C:\SE\tools')
from dbhelps import api, torchlog  # noqa: E402

SERVER = r'C:\SE'
CONFIG = os.path.join(SERVER, 'Instance', 'SentisClusters.cfg')
MODES = {
    'off': {'Parallel': 'false', 'Detach': 'false'},
    'parallel': {'Parallel': 'true', 'Detach': 'false'},
    'detachonly': {'Parallel': 'false', 'Detach': 'true'},
    'detach': {'Parallel': 'true', 'Detach': 'true'},
}
TITLES = {'off': 'clusters off', 'parallel': 'parallel clusters', 'detachonly': 'detach only', 'detach': 'parallel + detach'}


def ps(command):
    return subprocess.run(['powershell', '-NoProfile', '-Command', command], capture_output=True, text=True).stdout


def set_config(values):
    text = open(CONFIG, encoding='utf-8').read()
    for key, value in values.items():
        text, n = re.subn(r'<%s>\w+</%s>' % (key, key), '<%s>%s</%s>' % (key, value, key), text)
        if n != 1:
            sys.exit('no <%s> in %s' % (key, CONFIG))
    open(CONFIG, 'w', encoding='utf-8').write(text)


def read_config():
    text = open(CONFIG, encoding='utf-8').read()
    return {key: re.search(r'<%s>(\w+)</%s>' % (key, key), text).group(1) for key in ('Parallel', 'Detach')}


def ready():
    try:
        return 'error' not in api('status', timeout=5)
    except Exception:
        return False


def warm():
    # SentisClusters says "warm up" in its status for the first minute after the start: nothing detaches then
    try:
        answers = api('command', {'text': '!clusters status'}).get('answers', [])
        return not any('warm up' in a for a in answers)
    except Exception:
        return False


def restart(values):
    if ready():
        subprocess.run([sys.executable, os.path.join(SERVER, 'tools', 'save_world.py')], capture_output=True, timeout=120)
    ps(r"Get-Process Torch.Server -ErrorAction SilentlyContinue | Where-Object { $_.Path -like 'C:\SE\*' } | Stop-Process -Force")
    time.sleep(5)
    set_config(values)
    before = len(torchlog().splitlines())
    ps(r"$env:COMPlus_GCgen0MaxBudget = '2000000'; Start-Process -FilePath C:\SE\Torch.Server.exe -WorkingDirectory C:\SE -ArgumentList '-noupdate'")
    t0 = time.time()
    while time.time() - t0 < 900:
        lines = torchlog().splitlines()[before:]
        if any('SentisWatcher: recording' in l for l in lines) and ready() and warm():
            return
        time.sleep(10)
    sys.exit('the server did not come up in 15 minutes')


def run(scenario):
    start = len(torchlog().splitlines())
    api('test', {'name': scenario})
    t0 = time.time()
    while time.time() - t0 < 1200:
        for line in torchlog().splitlines()[start:]:
            if re.search(r'\[TEST\] (PASSED|FAILED) %s \(' % re.escape(scenario), line):
                return line
        time.sleep(10)
    return 'no verdict in 20 minutes'


def parse(line):
    result = {'raw': line}
    speeds = re.search(r'sim speed by probe: ([^|]*)\|', line)
    if speeds:
        for part in speeds.group(1).split(','):
            m = re.match(r'\s*(\S+) ([\d.]+|NaN)(.*)', part)
            if m:
                result[m.group(1)] = (m.group(2), m.group(3).strip())
    for key, pattern in (('welded', r'welded (\d+) blocks'), ('fps', r'fps=([\d.]+)'), ('avg', r'frame avg=([\d.]+)ms'),
                         ('p95', r'p95=([\d.]+)'), ('p99', r'p99=([\d.]+)'), ('max', r'max=([\d.]+)ms')):
        m = re.search(pattern, line)
        result[key] = m.group(1) if m else '?'
    return result


def main():
    modes = sys.argv[1].split(',') if len(sys.argv) > 1 and sys.argv[1] else ['off', 'parallel', 'detachonly', 'detach']
    scenario = sys.argv[2] if len(sys.argv) > 2 else 'heavy_neighbour'
    original = read_config()
    results = {}
    try:
        for mode in modes:
            print('== %s: restarting' % TITLES[mode], flush=True)
            restart(MODES[mode])
            print('   running ' + scenario, flush=True)
            results[mode] = parse(run(scenario))
            r = results[mode]
            print('   ' + ', '.join('%s %s' % (k, v[0]) for k, v in r.items() if isinstance(v, tuple)) +
                  ', fps %s, frame avg %s ms, p99 %s ms, welded %s' % (r['fps'], r['avg'], r['p99'], r['welded']), flush=True)
    finally:
        print('== putting the config back (%s) and restarting' % original, flush=True)
        restart(original)

    names = sorted({k for r in results.values() for k, v in r.items() if isinstance(v, tuple)}, key=lambda n: (n != 'heavy', n))
    print()
    print('%-22s' % '' + ''.join('%20s' % TITLES[m] for m in modes))
    for name in names:
        print('%-22s' % ('sim speed ' + name) + ''.join('%20s' % (results[m].get(name, ('?', ''))[0] + (' *' if results[m].get(name, ('', ''))[1] else '')) for m in modes))
    for key, title in (('fps', 'server fps'), ('avg', 'frame avg, ms'), ('p95', 'frame p95, ms'), ('p99', 'frame p99, ms'),
                       ('max', 'frame max, ms'), ('welded', 'welded in 60 s')):
        print('%-22s' % title + ''.join('%20s' % results[m][key] for m in modes))
    print('(* the probe was in a planet\'s gravity: its speed says more than the simulation)')
    out = os.path.join(SERVER, 'Logs', 'clusters_compare_%s_%s.json' % (scenario, time.strftime('%Y%m%d_%H%M%S')))
    json.dump(results, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
    print('the result lines: ' + out)


if __name__ == '__main__':
    main()
