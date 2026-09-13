#!/usr/bin/env python3
"""Run a deliberately constrained EMA-pullback template sweep on diagnostic bars.

The template is the current 8-EMA concept generalized to a fast/intermediate/
slow EMA triple: ordered fan, strong recent fast/intermediate slopes, a two-bar
counter-trend pullback preceded by two trend bars, controlled fast-EMA touch,
trend-side close, and local displacement. It is a hypothesis generator, not a
license to select the best in-sample row.
"""
import argparse, csv, math
from datetime import date
from pathlib import Path

def key(t):
    d=date(int(t[:4]),int(t[5:7]),int(t[8:10])); return d.toordinal()*86400+int(t[11:13])*3600+int(t[14:16])*60+int(t[17:19])

def ema(values, length):
    a=2.0/(length+1); out=[]; value=values[0]
    for x in values: value=a*x+(1-a)*value; out.append(value)
    return out

def slope(series, i, ticks):
    return math.degrees(math.atan2(series[i]-series[i-3],3*ticks))

def outcome(rows, i, direction, ticks, horizon=12, target=5, stop=10):
    entry=rows[i]['close']
    for f in rows[i+1:i+horizon+1]:
        good=((f['high']-entry)/ticks if direction>0 else (entry-f['low'])/ticks)
        bad=((f['low']-entry)/ticks if direction>0 else (entry-f['high'])/ticks)
        if bad <= -stop: return 'STOP' # conservative if both occur in the bar
        if good >= target: return 'TARGET'
    return 'NONE'

def qualifies(rows, f, s, t, i, ticks):
    if i < 12 or i+12 >= len(rows): return 0
    fast,slow,trend=f[i],s[i],t[i]
    direction=1 if fast>slow else -1 if fast<slow else 0
    if not direction: return 0
    gap=rows[i]['range']*0.5
    ordered=(fast>slow>trend) if direction>0 else (fast<slow<trend)
    fs=(fast-slow)/ticks if direction>0 else (slow-fast)/ticks
    st=(slow-trend)/ticks if direction>0 else (trend-slow)/ticks
    ft=(fast-trend)/ticks if direction>0 else (trend-fast)/ticks
    if not ordered or abs(fast-slow)/ticks < 4 or fs < gap or not (st >= gap or ft >= 2*rows[i]['range']): return 0
    best_f=max(slope(f,i-b,ticks)*direction for b in range(7))
    best_s=max(slope(s,i-b,ticks)*direction for b in range(7))
    if best_f < 39 or best_s < 39: return 0
    pullback=(rows[i-1]['close']<rows[i-1]['open'] and rows[i-2]['close']<rows[i-2]['open'] and rows[i-3]['close']>rows[i-3]['open'] and rows[i-4]['close']>rows[i-4]['open']) if direction>0 else (rows[i-1]['close']>rows[i-1]['open'] and rows[i-2]['close']>rows[i-2]['open'] and rows[i-3]['close']<rows[i-3]['open'] and rows[i-4]['close']<rows[i-4]['open'])
    if not pullback: return 0
    crosses=rows[i]['low']<=fast<=rows[i]['high']
    penetration=(fast-rows[i]['low'])/ticks if direction>0 else (rows[i]['high']-fast)/ticks
    close_side=rows[i]['close']>=fast if direction>0 else rows[i]['close']<=fast
    color=rows[i]['close']>=rows[i]['open'] if direction>0 else rows[i]['close']<=rows[i]['open']
    reference=min(rows[i-1]['low'],rows[i-2]['low']) if direction>0 else max(rows[i-1]['high'],rows[i-2]['high'])
    displacement=(reference-rows[i]['low'])/ticks if direction>0 else (rows[i]['high']-reference)/ticks
    return direction if crosses and 0 <= penetration <= 4.5 and close_side and color and displacement >= 1 else 0

def main():
    p=argparse.ArgumentParser(); p.add_argument('--diagnostic',type=Path,required=True); p.add_argument('--output',type=Path,required=True); p.add_argument('--from',dest='start',required=True); p.add_argument('--fast',default='5,8,11,13'); p.add_argument('--slow',default='18,24,30,36'); p.add_argument('--trend',default='40,50,60'); a=p.parse_args()
    with a.diagnostic.open(newline='',encoding='utf-8-sig') as h:
        rows=[{'time':key(x['Time']),'open':float(x['Open']),'high':float(x['High']),'low':float(x['Low']),'close':float(x['Close']),'range':float(x['RangeTicks'])} for x in csv.DictReader(h)]
    start=key(a.start); ticks=.25; closes=[r['close'] for r in rows]; fs=[int(x) for x in a.fast.split(',')]; ss=[int(x) for x in a.slow.split(',')]; ts=[int(x) for x in a.trend.split(',')]; cache={n:ema(closes,n) for n in set(fs+ss+ts)}; results=[]
    for fl in fs:
      for sl in ss:
       for tl in ts:
        if not fl<sl<tl: continue
        n=target=stop=none=0
        for i,r in enumerate(rows):
            if r['time'] < start or r['range'] != 5: continue
            d=qualifies(rows,cache[fl],cache[sl],cache[tl],i,ticks)
            if d:
                n+=1; o=outcome(rows,i,d,ticks)
                if o=='TARGET': target+=1
                elif o=='STOP': stop+=1
                else: none+=1
        results.append((fl,sl,tl,n,target,stop,none,100*target/n if n else 0))
    a.output.parent.mkdir(parents=True,exist_ok=True)
    with a.output.open('w',newline='') as h:
        w=csv.writer(h); w.writerow(['FastEMA','SlowEMA','TrendEMA','Candidates','Target','Stop','None','TargetRatePct']); w.writerows(results)
    print('wrote',a.output); print('Fast,Slow,Trend,Candidates,TargetRatePct')
    for r in sorted(results,key=lambda x:(-x[7],-x[3])): print('%d,%d,%d,%d,%.2f'% (r[0],r[1],r[2],r[3],r[7]))
if __name__=='__main__': main()
