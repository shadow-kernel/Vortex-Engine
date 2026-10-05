#!/bin/bash
# Bring an EXISTING Vortex project up to the current content of the template it was created from: scripts, prefabs,
# materials, textures, models, audio and scenes are copied in (nothing you added is deleted; files with the same name
# are overwritten — your old scripts and project.vortex are backed up first), and the scene list of project.vortex is
# merged. The template defaults to the Horror Starter; pass another one as the second argument.
#   tools/upgrade-project-from-template.sh <path-to-your-project> [path-to-template]
#   tools/upgrade-project-from-template.sh ~/VortexEngineProjects/MyRange Templates/TacticalShooter
set -e
PROJ="$1"; ROOT="$(cd "$(dirname "$0")/.." && pwd)"; TPL="${2:-$ROOT/Templates/HorrorStarter}"
[ -f "$PROJ/project.vortex" ] || { echo "not a Vortex project: $PROJ"; exit 1; }
[ -f "$TPL/template.json" ] || { echo "not a template folder: $TPL"; exit 1; }
BACKUP="$PROJ/.ve/backup-$(date +%Y%m%d-%H%M%S)"; mkdir -p "$BACKUP"
cp -R "$PROJ/Assets/Scripts" "$BACKUP/Scripts" 2>/dev/null || true; cp "$PROJ/project.vortex" "$BACKUP/" 2>/dev/null || true
for d in Scripts Prefabs Materials Textures Models Audio Scenes; do
  [ -d "$TPL/Assets/$d" ] || continue
  mkdir -p "$PROJ/Assets/$d"; rsync -a "$TPL/Assets/$d/" "$PROJ/Assets/$d/"
done
for f in WEAPONS_GUIDE.md CHARACTER_SETUP_GUIDE.md ATTRIBUTIONS.txt; do [ -f "$TPL/$f" ] && cp "$TPL/$f" "$PROJ/$f"; done
python3 - "$PROJ/project.vortex" "$TPL/project.vortex" <<'PY'
import json, sys
proj, tpl = sys.argv[1], sys.argv[2]
p = json.loads(open(proj, encoding='utf-8-sig').read()); t = json.loads(open(tpl, encoding='utf-8-sig').read())
have = {s['path'] for s in p.get('scenes', [])}
for s in t.get('scenes', []):
    if s['path'] not in have: p.setdefault('scenes', []).append(s)
if t.get('startSceneId'): p['startSceneId'] = t['startSceneId']
p['engineVersion'] = t.get('engineVersion', p.get('engineVersion'))
open(proj, 'w', encoding='utf-8').write(json.dumps(p, indent=2))
print('scenes now:', [s['name'] for s in p['scenes']], '| start scene:', p.get('startSceneId'))
PY
echo "upgraded $PROJ (backup of your scripts + manifest in $BACKUP)"
