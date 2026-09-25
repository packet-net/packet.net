#!/usr/bin/env python3
"""Field-by-field comparison of Packet.Aprs against Ham::APRS::FAP (the aprs.fi parser).

Usage:
  aprs-corpus dump lines.bin ours.jsonl
  perl tools/Packet.Aprs.Corpus/fap/fap-decode.pl < lines.bin > fap.jsonl
  tools/Packet.Aprs.Corpus/fap/compare-fap.py lines.bin ours.jsonl fap.jsonl

Prints, for every field, how many packets both decoders accepted, how many agree, and samples of
disagreements. FAP reports metric units (km/h, metres, Celsius, m/s, mm); ours are APRS's on-air
units, converted here.
"""
import json, sys, collections, math

lines = open(sys.argv[1], 'rb').read().split(b'\n')
ours = [json.loads(l) for l in open(sys.argv[2], encoding='utf-8')]
fap = [json.loads(l) for l in open(sys.argv[3], encoding='utf-8', errors='replace')]

KN_KMH, FT_M, MPH_MS, IN_MM = 1.852, 0.3048, 0.44704, 25.4
checked = collections.Counter()
bad = collections.defaultdict(list)


def cmp(field, line, o, f, ok):
    checked[field] += 1
    if not ok:
        bad[field].append((line, o, f))


def close(a, b, tol):
    return a is not None and b is not None and abs(float(a) - float(b)) <= tol


def num(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return None


POSITIONED = {'AprsPositionReport', 'AprsObjectReport', 'AprsItemReport', 'AprsMicEReport'}
for raw, o, f in zip(lines, ours, fap):
    line = raw.decode('latin1')
    if 'resultcode' in f or o['type'].startswith(('unrecognized', 'header')):
        continue
    t = o['type']
    if t in POSITIONED and 'latitude' in f:
        cmp('latitude', line, o.get('lat'), f['latitude'], close(o.get('lat'), f['latitude'], 1e-6))
        cmp('longitude', line, o.get('lon'), f['longitude'], close(o.get('lon'), f['longitude'], 1e-6))
        fsym = (f.get('symboltable') or '') + (f.get('symbolcode') or '')
        if fsym[:1] in 'abcdefghij' and o.get('compressed'):
            fsym = chr(ord(fsym[0]) - ord('a') + ord('0')) + fsym[1:]
        cmp('symbol', line, o.get('sym'), fsym, o.get('sym') == fsym)
        ospeed = o.get('speed_kn')
        cmp('speed', line, ospeed, f.get('speed'),
            (ospeed is None and f.get('speed') is None) or close(None if ospeed is None else ospeed * KN_KMH, f.get('speed'), 0.01))
        ocourse = o.get('course') or 0
        cmp('course', line, o.get('course'), f.get('course'), ocourse == (f.get('course') or 0))
        oalt = o.get('alt_ft')
        cmp('altitude', line, oalt, f.get('altitude'),
            (oalt is None and f.get('altitude') is None) or close(None if oalt is None else oalt * FT_M, f.get('altitude'), 0.05))
        oc, fc = (o.get('comment') or '').strip(), (f.get('comment') or '').strip()
        # Presentation differences: FAP leaves the Mic-E device codes and the voice frequency in the
        # comment, and strips the delimiter after a data extension only when it is '/'.
        if t == 'AprsMicEReport':
            prefix = o.get('mice_type') or ''
            if prefix and fc.startswith(prefix):
                fc = fc[len(prefix):].strip()
            suffix = (o.get('mice_suffix') or '').strip()
            if suffix and fc.endswith(suffix):
                fc = fc[:-len(suffix)].strip()
        if 'freq' in o:
            same = fc.endswith(oc)
        else:
            same = oc == fc or oc == fc.lstrip('/ ')
        if 'wx' not in f:
            cmp('comment', line, oc, fc, same)
        cmp('phg', line, o.get('phg'), f.get('phg'), (o.get('phg') or '') == (f.get('phg') or '')[:4])
        if t in ('AprsObjectReport', 'AprsItemReport'):
            fname = (f.get('objectname') or f.get('itemname') or '').rstrip()
            cmp('name', line, o.get('name'), fname, (o.get('name') or '').rstrip() == fname)
            cmp('alive', line, o.get('alive'), f.get('alive'), bool(o.get('alive')) == bool(f.get('alive')))
        if t == 'AprsPositionReport':
            cmp('messaging', line, o.get('messaging'), f.get('messaging'), bool(o.get('messaging')) == bool(f.get('messaging')))
        if 'wx' in f:
            w = f['wx']
            for ours_key, fap_key, conv, tol in [
                ('wx_dir', 'wind_direction', 1, 0.5), ('wx_speed_mph', 'wind_speed', MPH_MS, 0.06),
                ('wx_gust_mph', 'wind_gust', MPH_MS, 0.06), ('wx_hum', 'humidity', 1, 0.5),
                ('wx_pres', 'pressure', 1, 0.06), ('wx_rain1h_in', 'rain_1h', IN_MM, 0.06),
                ('wx_rain24h_in', 'rain_24h', IN_MM, 0.06), ('wx_rainmid_in', 'rain_midnight', IN_MM, 0.06),
                ('wx_lum', 'luminosity', 1, 0.5)]:
                ov, fv = o.get(ours_key), num(w.get(fap_key))
                cmp('wx.' + fap_key, line, ov, fv, (ov is None and fv is None) or close(None if ov is None else ov * conv, fv, tol))
            ot, ft = o.get('wx_temp_f'), num(w.get('temp'))
            cmp('wx.temp', line, ot, ft, (ot is None and ft is None) or close(None if ot is None else (ot - 32) / 1.8, ft, 0.06))
    elif t in ('AprsTextMessage', 'AprsMessageAck', 'AprsMessageReject', 'AprsBulletin', 'AprsNwsBulletin') and f.get('type') == 'message':
        cmp('msg.to', line, o.get('to'), f.get('destination'), o.get('to') == (f.get('destination') or '').rstrip())
        if t == 'AprsMessageAck':
            cmp('msg.ack', line, o.get('ack'), f.get('messageack'), o.get('ack') == f.get('messageack'))
        elif t == 'AprsMessageReject':
            cmp('msg.rej', line, o.get('rej'), f.get('messagerej'), o.get('rej') == f.get('messagerej'))
        else:
            cmp('msg.text', line, o.get('text'), f.get('message'), (o.get('text') or '') == (f.get('message') or ''))
            cmp('msg.id', line, o.get('msgid'), f.get('messageid'), (o.get('msgid') or '') == (f.get('messageid') or ''))
    elif t == 'AprsStatusReport' and f.get('type') == 'status':
        cmp('status', line, o.get('status'), f.get('status'), (o.get('status') or '').strip() == (f.get('status') or '').strip())
    elif t == 'AprsTelemetryReport' and 'telemetry' in f:
        tl = f['telemetry']
        oseq, fseq = o.get('tlm_seq'), tl.get('seq')
        cmp('tlm.seq', line, oseq, fseq, (num(oseq) is not None and num(oseq) == num(fseq)) or str(oseq) == str(fseq))
        ovals = [num(v) for v in (o.get('tlm_vals') or '').split(',') if v != '']
        fvals = [num(v) for v in (tl.get('vals') or []) if v is not None]
        cmp('tlm.vals', line, ovals, fvals, ovals[:len(fvals)] == fvals)

print(f"{'field':<22}{'compared':>10}{'differ':>9}")
for field in sorted(checked):
    print(f"{field:<22}{checked[field]:>10}{len(bad[field]):>9}")
for field in sorted(bad, key=lambda k: -len(bad[k])):
    print(f"\n== {field}: {len(bad[field])} differ")
    for line, o, f in bad[field][:5]:
        print(f"   {line[:170]}\n      ours={o!r}  fap={f!r}")
