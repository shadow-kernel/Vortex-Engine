"""Compose the v3.0 trailer (docs/showcase/vortex-3.0-claude.webp) from a VORTEX_SHOWCASE_SESSION recording plus Claude
Code's timestamped stream-json.

Recording: start the editor with VORTEX_SHOWCASE=<dir>/rec VORTEX_SHOWCASE_SESSION=210 (and a private VORTEX_APPDATA_DIR
whose editor-prefs.json enables the MCP server), then run Claude Code against it and write every stdout line as
{"t": <unix time>, "line": <stream-json line>} to <dir>/claude_ts.jsonl. Needs Pillow, img2webp (libwebp) and macOS
system fonts (SFNS).

  python3 tools/showcase/compose_session.py <dir>

Reads  <dir>/rec/session/{f0000.png, v0000.bmp, meta.json}  (window frames + native viewport frames)
       <dir>/claude_ts.jsonl                                 ({"t": unix time, "line": stream-json line})
Writes <dir>/frames/*.png and <dir>/vortex-3.0-claude.webp (img2webp).
"""
import glob, json, os, subprocess, sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter

D = sys.argv[1]
REC = os.path.join(D, 'rec', 'session')
OUT = os.path.join(D, 'frames')
W, H = 1280, 720
LOGO = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'Managed', 'Vortex.Editor', 'Assets', 'Logo.png')
SANS = '/System/Library/Fonts/SFNS.ttf'
MONO = '/System/Library/Fonts/SFNSMono.ttf'
ACCENT = (122, 108, 240)
INK = (236, 236, 242)
DIM = (160, 160, 175)


def font(path, size):
    return ImageFont.truetype(path, size)


# ---------------------------------------------------------------- Claude's tool calls on a clock
calls = []          # (t, tool, detail)
result = None
for raw in open(os.path.join(D, 'claude_ts.jsonl')):
    rec = json.loads(raw)
    try:
        d = json.loads(rec['line'])
    except Exception:
        continue
    if d.get('type') == 'assistant':
        for c in d['message'].get('content', []):
            if c.get('type') != 'tool_use' or not c['name'].startswith('mcp__vortex__'):
                continue
            inp = c.get('input') or {}
            detail = inp.get('name') or inp.get('entity') or inp.get('script') or inp.get('prompt') or inp.get('kind') or ''
            if isinstance(detail, list):
                detail = ', '.join(str(x) for x in detail[:3])
            calls.append((rec['t'], c['name'][len('mcp__vortex__'):], str(detail)[:60]))
    elif d.get('type') == 'result':
        result = d
calls.sort()
t_first = calls[0][0] if calls else 0

# ---------------------------------------------------------------- frames
meta = json.load(open(os.path.join(REC, 'meta.json')))
vx, vy, vw, vh = meta['viewport'] if meta.get('viewport') else (0, 0, 0, 0)
frames = sorted(glob.glob(os.path.join(REC, 'f*.png')))
os.makedirs(OUT, exist_ok=True)
for f in glob.glob(os.path.join(OUT, '*.png')):
    os.remove(f)


def composite(i):
    win = Image.open(frames[i]).convert('RGB')
    vp = os.path.join(REC, 'v%04d.bmp' % i)
    if vw > 0 and os.path.exists(vp):
        v = Image.open(vp).convert('RGB').resize((int(round(vw)), int(round(vh))), Image.LANCZOS)
        win.paste(v, (int(round(vx)), int(round(vy))))
    return win.resize((W, H), Image.LANCZOS)


def caption(img, tool, detail, n, total, elapsed):
    d = ImageDraw.Draw(img, 'RGBA')
    d.rectangle([0, H - 64, W, H], fill=(14, 14, 22, 228))
    d.rectangle([0, H - 64, 6, H], fill=ACCENT + (255,))
    d.text((24, H - 54), 'Claude Code', font=font(SANS, 15), fill=DIM)
    d.text((24, H - 34), 'mcp__vortex__' + tool, font=font(MONO, 19), fill=INK)
    if detail:
        x = 24 + d.textlength('mcp__vortex__' + tool, font=font(MONO, 19)) + 14
        d.text((x, H - 32), '“' + detail + '”', font=font(SANS, 17), fill=DIM)
    right = 'tool call %d of %d  ·  %d:%02d' % (n, total, elapsed // 60, elapsed % 60)
    d.text((W - 24 - d.textlength(right, font=font(SANS, 16)), H - 42), right, font=font(SANS, 16), fill=DIM)
    return img


def card(bg, lines, logo=True):
    img = bg.copy().filter(ImageFilter.GaussianBlur(14))
    d = ImageDraw.Draw(img, 'RGBA')
    d.rectangle([0, 0, W, H], fill=(8, 8, 14, 196))
    y = 200
    if logo and os.path.exists(LOGO):
        lg = Image.open(LOGO).convert('RGBA').resize((84, 84), Image.LANCZOS)
        img.paste(lg, ((W - 84) // 2, y - 110), lg)
    for text, size, color, face in lines:
        f = font(face, size)
        d.text(((W - d.textlength(text, font=f)) / 2, y), text, font=f, fill=color)
        y += int(size * 1.45)
    return img


total = len(calls)
seq = []   # (path, duration ms)
first = composite(0)
title = card(first, [
    ('Vortex Engine 3.0', 54, INK, SANS),
    ('Claude builds with you', 30, ACCENT, SANS),
    ('', 16, INK, SANS),
    ('One prompt to Claude Code: “build a short horror corridor and fully sound it”', 19, DIM, SANS),
    ('Claude drives the editor through its MCP server: 68 tools, every step undoable', 19, DIM, SANS),
])
p = os.path.join(OUT, 'a_title.png'); title.save(p); seq.append((p, 3600))

shown = 0
for i, f in enumerate(frames):
    t = os.path.getmtime(f)
    if calls and t < t_first - 1:
        continue                      # before Claude's first call: nothing happens yet
    done = [c for c in calls if c[0] <= t]
    if calls and len(done) == total and t > calls[-1][0] + 6:
        break                         # Claude is done; the end card takes over
    tool, detail = (done[-1][1], done[-1][2]) if done else ('get_editor_info', '')
    img = caption(composite(i), tool, detail, len(done), total, int(max(0, t - t_first)))
    p = os.path.join(OUT, 'f%04d.png' % i); img.save(p); seq.append((p, 150)); shown += 1
    last = img

minutes = (calls[-1][0] - t_first) / 60.0 if calls else 0
end = card(composite(min(i, len(frames) - 1)), [
    ('A sounded horror corridor in %.1f minutes' % minutes, 36, INK, SANS),
    ('%d tool calls · flickering lights · C# scripts · ambience · footstep container · scare trigger' % total, 19, DIM, SANS),
    ('Undo any step — or revert it in Tools ▸ Claude ▸ Operations', 19, DIM, SANS),
    ('', 14, INK, SANS),
    ('github.com/shadow-kernel/Vortex-Engine', 22, ACCENT, MONO),
], logo=True)
p = os.path.join(OUT, 'z_end.png'); end.save(p); seq.append((p, 4500))

args = ['img2webp', '-loop', '0', '-mixed', '-q', '78', '-m', '6']
for path, ms in seq:
    args += ['-d', str(ms), path]
target = os.path.join(D, 'vortex-3.0-claude.webp')
args += ['-o', target]
subprocess.run(args, check=True, stdout=subprocess.DEVNULL)
print('frames', shown, 'of', len(frames), '| calls', total, '| %.1f min' % minutes, '|', target, os.path.getsize(target) // 1024, 'KB')
