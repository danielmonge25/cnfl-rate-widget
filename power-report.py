"""Optional power report: builds report.html from logs/power-*.csv (written once a minute by
power-log.ps1) and opens it. Needs Python 3.8+; the widget's "Report" link runs it.

Costs use the rates in settings.json (CNFL T-REH): each logged minute is priced at the rate of
its time of day, exactly like the widget's "PC today / This month" numbers. Watts are partly
estimated (see power-log.ps1), so treat the colones as a good guide, not your meter's figure.
    --no-open    write report.html without opening it
"""
import csv
import datetime as dt
import html
import json
import sys
import webbrowser
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
NO_OPEN = '--no-open' in sys.argv
BANDS = ('high', 'mid', 'low')
LABEL = {'high': 'High', 'mid': 'Mid', 'low': 'Low'}


def settings():
    """settings.default.json with settings.json on top, like settings.ps1."""
    cfg = json.loads((HERE / 'settings.default.json').read_text(encoding='utf-8-sig'))
    user_file = HERE / 'settings.json'
    if user_file.exists():
        for k, v in json.loads(user_file.read_text(encoding='utf-8-sig')).items():
            if isinstance(v, dict) and isinstance(cfg.get(k), dict):
                cfg[k].update(v)
            else:
                cfg[k] = v
    return cfg


CFG = settings()
OVER_500 = bool(CFG.get('useAbove500kWhRates'))
RATES = CFG['ratesAbove500kWh'] if OVER_500 else CFG['rates']


def band(t):
    """T-REH: High 10:01-12:30 and 17:31-20:00 on weekdays, Mid 6:01-20:00 otherwise, Low 20:01-6:00."""
    m = t.hour * 60 + t.minute
    if 601 <= m < 751 or 1051 <= m < 1201:
        return 'high' if t.weekday() < 5 else 'mid'
    return 'mid' if 361 <= m < 1201 else 'low'


def activity(r):
    """What the minute's power mostly went to."""
    name, _, pct = (r['top_cpu'].split(';')[0] if r['top_cpu'] else '').partition(':')
    gpu_busy = float(r['gpu_util']) >= 30
    if name and (int(pct or 0) >= 10 or gpu_busy):
        return name
    if gpu_busy and r['foreground']:
        return r['foreground']
    return 'idle / light use'


def load_rows():
    rows = []
    for f in sorted((HERE / 'logs').glob('power-*.csv')):
        with open(f, encoding='utf-8-sig', newline='') as fh:
            for r in csv.DictReader(fh):
                try:
                    r['t'] = dt.datetime.strptime(r['time'], '%Y-%m-%d %H:%M')
                    r['kwh'] = (float(r['pc_w']) + float(r['mon_w'])) / 60000
                except (ValueError, KeyError):
                    continue
                r['band'] = band(r['t'])
                rows.append(r)
    return rows


def crc(x):
    return f"₡{x:,.0f}"


def main():
    if sys.stdout:   # None under pythonw; Windows consoles can't print ₡ by default
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
    rows = load_rows()
    if not rows:
        print('No data yet: the power log writes one row a minute. Check back later.')
        return
    days = defaultdict(lambda: {'min': 0, 'kwh': defaultdict(float)})
    high_acts = defaultdict(lambda: [0, 0.0])   # last 7 days: activity -> [minutes, kWh]
    week_ago = rows[-1]['t'] - dt.timedelta(days=7)
    for r in rows:
        d = days[r['t'].date()]
        d['min'] += 1
        d['kwh'][r['band']] += r['kwh']
        if r['band'] == 'high' and r['t'] >= week_ago:
            a = high_acts[activity(r)]
            a[0] += 1
            a[1] += r['kwh']

    def cost(d):
        return sum(d['kwh'][b] * RATES[b] for b in BANDS)

    today = rows[-1]['t'].date()
    month = [d for k, d in days.items() if (k.year, k.month) == (today.year, today.month)]
    m_kwh = sum(sum(d['kwh'].values()) for d in month)
    m_cost = sum(cost(d) for d in month)
    full = [d for k, d in days.items() if k != today and k != min(days)]   # whole days only
    per_day = sum(cost(d) for d in full) / len(full) if full else None

    day_rows = []
    for k in sorted(days, reverse=True):
        d = days[k]
        kwh = sum(d['kwh'].values())
        c = cost(d)
        high_share = d['kwh']['high'] * RATES['high'] / c * 100 if c else 0
        cells = ''.join(f"<td>{crc(d['kwh'][b] * RATES[b])}</td>" for b in BANDS)
        day_rows.append(f"<tr><td>{k:%a %d %b}</td><td>{d['min'] / 60:.1f} h</td><td>{kwh:.2f}</td>{cells}"
                        f"<td><b>{crc(c)}</b></td><td>{high_share:.0f}%</td></tr>")

    acts = sorted(high_acts.items(), key=lambda kv: -kv[1][1])[:8]
    act_rows = ''.join(f"<tr><td>{html.escape(a)}</td><td>{m / 60:.1f} h</td><td>{k:.2f}</td><td>{crc(k * RATES['high'])}</td></tr>"
                       for a, (m, k) in acts) or '<tr><td colspan="4">No High-rate use logged in the last 7 days.</td></tr>'

    block = 'above-500 kWh rates' if OVER_500 else '0–500 kWh rates'
    rates_txt = ' · '.join(f"{LABEL[b]} ₡{RATES[b]:.2f}/kWh" for b in BANDS)
    proj = f"About <b>{crc(per_day * 30)}</b> a month at the average of {len(full)} full day(s) logged." if per_day else 'A monthly projection appears after the first full day.'
    page = f"""<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>PC Power Report</title><style>
:root {{ --bg:#fafaf8; --fg:#1d1d1b; --muted:#6b6b66; --line:#e2e2dc; --accent:#b4441f; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#161615; --fg:#ececea; --muted:#a3a39d; --line:#2e2e2b; --accent:#f08a5d; }} }}
body {{ background:var(--bg); color:var(--fg); font:15px/1.5 system-ui, sans-serif; margin:0; padding:24px 16px; }}
main {{ max-width:860px; margin:0 auto; }} h1 {{ font-size:22px; margin:0 0 4px; }} h2 {{ font-size:16px; margin:28px 0 8px; }}
.muted {{ color:var(--muted); font-size:13px; }} .big {{ font-size:28px; font-weight:650; color:var(--accent); }}
.wrap {{ overflow-x:auto; }} table {{ border-collapse:collapse; width:100%; font-variant-numeric:tabular-nums; }}
th, td {{ text-align:right; padding:6px 10px; border-bottom:1px solid var(--line); white-space:nowrap; }}
th:first-child, td:first-child {{ text-align:left; }} th {{ color:var(--muted); font-weight:500; font-size:13px; }}
</style></head><body><main>
<h1>PC power report</h1>
<div class="muted">Updated {rows[-1]['t']:%a %d %b %H:%M} · CNFL T-REH, {block}: {rates_txt}</div>
<h2>This month so far</h2>
<div class="big">{crc(m_cost)}</div>
<div>{m_kwh:.1f} kWh over {sum(d['min'] for d in month) / 60:.0f} hours on. {proj}</div>
<h2>By day</h2><div class="wrap"><table>
<tr><th>Day</th><th>On</th><th>kWh</th><th>High</th><th>Mid</th><th>Low</th><th>Total</th><th>High share</th></tr>
{''.join(day_rows)}</table></div>
<h2>What used power during High-rate hours (last 7 days)</h2>
<div class="muted">Weekdays 10:01–12:30 and 17:31–20:00. The same use after 20:01 costs about a sixth as much.</div>
<div class="wrap"><table><tr><th>Activity</th><th>Time</th><th>kWh</th><th>Cost</th></tr>{act_rows}</table></div>
<p class="muted">NVIDIA GPU power is measured; the CPU, the rest of the PC and the screens are estimated (powerLog in settings.json).
Hours when the PC was off or asleep cost nothing and aren't logged.</p>
</main></body></html>"""
    out = HERE / 'report.html'
    out.write_text(page, encoding='utf-8')
    print(f"This month: {m_kwh:.1f} kWh, {crc(m_cost)}. Report: {out}")
    if not NO_OPEN:
        webbrowser.open(out.as_uri())


if __name__ == '__main__':
    main()
