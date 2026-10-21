"""Skill audit: every active skill of a job line, what its description says vs. the data the server reads.
usage: python audit.py <job prefixes, comma separated, e.g. 64 or 1000,1100,1200> [--all-levels]
"""
import json, sys, os, re
sys.stdout.reconfigure(encoding='utf-8')
base = os.environ.get('RO3_AUDIT_DIR', os.path.dirname(os.path.abspath(__file__)))
data = r'D:\ro3emu-server\Client\RO3Emu\data'
texts = {int(k): v for k, v in json.load(open(base + r'\loc_en_ids.json', encoding='utf-8')).items()}
skills = json.load(open(data + r'\config\SkillConfig.json', encoding='utf-8'))
effects = json.load(open(data + r'\skills\Lua_Config_EffectData.json', encoding='utf-8'))
REL = {0: 'None', 1: 'Lock', 2: 'FixCircle', 3: 'FixRect', 4: 'Fan', 5: 'Arrow', 6: 'Rect360', 7: 'DragRect', 8: 'DragCircle', 9: 'LeftDrag'}
prefixes = sys.argv[1].split(',')


def fill(desc, row):
    vals = row.get('_kDescData') or []
    def rep(m):
        i = int(m.group(1)) - 1
        return f'Â«{vals[i]}Â»' if 0 <= i < len(vals) and vals[i] != '' else m.group(0)
    desc = re.sub(r'\$\{(\d+)\}', rep, desc)
    return re.sub(r'\^\{\d+\}', '', desc).replace('\\227\\128\\144', '[').replace('\\227\\128\\145', ']').replace('\\194\\176', 'Â°')


def timeline(show):
    p = os.path.join(data, 'skills', f'skill_{show}.json')
    if not os.path.exists(p):
        return None
    return json.load(open(p, encoding='utf-8')).get(str(show))


groups = {}
for r in skills.values():
    job = str(r.get('_iJob') or '')
    if not any(job.startswith(p) for p in prefixes) or r.get('_iBasicType') != 1:
        continue
    groups.setdefault(r['_iSkillID'], []).append(r)

for g in sorted(groups):
    rows = sorted(groups[g], key=lambda r: (r.get('_iMode') or 0, r['_iLevel']))
    base_rows = [r for r in rows if not r.get('_iMode')] or rows
    r = base_rows[-1]  # max level, base mode
    name = texts.get((r.get('_iName') or [0])[0], '?')
    desc = fill(texts.get((r.get('_iDescription') or [0])[0], ''), r)
    t = timeline(r.get('_iScriptShow')) or {}
    atk = [round(a, 2) for a in (t.get('m_kAtkPoints') or [])]
    shows = t.get('m_kShows', {}).get('1000000', [])
    bullets = [e for e, _ in shows if isinstance(effects.get(str(e), {}).get('m_kDamage'), dict) and 'm_kBulletData' in effects[str(e)]['m_kDamage']]
    moves = [e for e, _ in shows if effects.get(str(e), {}).get('m_kPoint', {}).get('m_bMovement')]
    modes = sorted({x.get('_iMode') for x in rows if x.get('_iMode')})
    print(f"## {g} {name}  (job {r.get('_iJob')}, script {r.get('_kScriptLogic')}, lv1-{r['_iLevel']}{', modes ' + str(modes) if modes else ''})")
    print(f"   {desc}")
    print(f"   release {r.get('_iRelease')}={REL.get(r.get('_iRelease'))} rangeType {r.get('_iRangeType')} kRange {r.get('_kRange')} dist {r.get('_iDistanceMax')} targets {r.get('_iTargetMax')} move {r.get('_bMovement')}")
    print(f"   P1 {r.get('_kDamageParam1')} P2 {r.get('_kDamageParam2')} show {r.get('_kShowDmg')} dmgType {r.get('_iDmgType')} ele {r.get('_iEleType')} extra {r.get('_kDamageParamExtra')} logic {r.get('_kLogicData')}")
    print(f"   buff1 {r.get('_kBuff1')} buff2 {r.get('_kBuff2')} chant {r.get('_iChantFix')}/{r.get('_iChantVar')} act {r.get('_iActionTime')} cd {r.get('_iCD')} desc {r.get('_kDescData')}")
    print(f"   timeline total {t.get('m_fTotalTime')} atk {atk} bullets {bullets} moveFx {moves} indicator {t.get('m_iSkillIndicator')}")
    print()

