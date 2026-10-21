import re, json, sys
sys.stdout.reconfigure(encoding='utf-8')
import os
base = os.environ.get('RO3_AUDIT_DIR', os.path.dirname(os.path.abspath(__file__)))
lines = open(base + r'\lua\Localization_en.lua', encoding='utf-8', errors='replace').read().splitlines()
texts = {}
cur_id = None
last_val = None
for line in lines:
    m = re.match(r'\s*L\d+_1 = (\d{6,})$', line)
    if m:
        last_val = int(m.group(1)); continue
    m = re.match(r'\s*L\d+_1 = (".*")$', line)
    if m:
        last_val = m.group(1); continue
    if line.strip().endswith('._iID = ' + line.strip().split(' = ')[-1]) and '._iID' in line:
        cur_id = last_val if isinstance(last_val, int) else None; continue
    if '._kDes' in line and cur_id is not None and isinstance(last_val, str):
        try:
            texts[cur_id] = json.loads(last_val)
        except Exception:
            texts[cur_id] = last_val.strip('"')
        cur_id = None
json.dump(texts, open(base + r'\loc_en_ids.json', 'w', encoding='utf-8'), ensure_ascii=False)
print(len(texts))
for k in (10110200089, 10110300075, 10110201830, 10110301025):
    print(k, texts.get(k))

