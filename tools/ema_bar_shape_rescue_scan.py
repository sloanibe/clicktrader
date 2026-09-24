#!/usr/bin/env python3
"""Constrained scan for bar-shape rules that rescue excluded EMA bounces."""
import argparse
import csv
from collections import defaultdict
from datetime import date
from pathlib import Path

TICK = .25


def key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return day.toordinal() * 86400 + int(value[11:13]) * 3600 + int(value[14:16]) * 60 + int(value[17:19])


def trend(row, direction):
    return row["c"] >= row["o"] if direction > 0 else row["c"] <= row["o"]


def counter(row, direction):
    return row["c"] < row["o"] if direction > 0 else row["c"] > row["o"]


def length(rows, i, direction, maximum):
    for n in range(1, maximum + 1):
        if all(counter(rows[i-j], direction) for j in range(1, n + 1)) and trend(rows[i-n-1], direction):
            return n
    return 0


def prior24(rows, i, direction):
    return max(direction * rows[i-j]["s24"] for j in range(1, 7))


def bar_shape(row, direction):
    tail = (row["o"] - row["l"]) / TICK if direction > 0 else (row["h"] - row["o"]) / TICK
    if tail <= .1:
        return "no_tail"
    if tail >= 4:
        return "tail_ge4"
    if tail >= 2:
        return "tail_2_3"
    return "tail_1"


def selected8(rows, i, direction, n):
    if n not in (1, 2): return False
    x = rows[i]; pen = -x["lo8"] if direction > 0 else x["hi8"]
    ref = min(rows[i-1]["l"], rows[i-2]["l"]) if direction > 0 else max(rows[i-1]["h"], rows[i-2]["h"])
    disp = (ref - x["l"]) / TICK if direction > 0 else (x["h"] - ref) / TICK
    return (x["x8"] and x["g824"] >= 5 and direction*x["s8"] >= 15 and
            prior24(rows, i, direction) >= (20 if n == 1 else 39) and
            1 <= pen <= (4.5 if n == 1 else 2.5) and disp >= 1)


def selected24(rows, i, direction, n):
    if n not in (1, 2, 3): return False
    cfg = {1:(45,10,3,3,0), 2:(30,15,1.5,3,2), 3:(39,0,1.5,1.5,1)}[n]
    x=rows[i]; deep=max((-rows[i-j]["lo24"] if direction>0 else rows[i-j]["hi24"]) for j in range(n+1)); rec=direction*(x["c"]-rows[i-1]["c"])/TICK
    return (prior24(rows,i,direction)>=cfg[0] and direction*x["s24"]>=cfg[1] and
            x["g824"]>=cfg[2] and direction*x["g2450"]>=cfg[3] and rec>=cfg[4] and 0<=deep<=5)


def rate(count, wins): return round(100*wins/count, 2) if count else 0


def main():
    p=argparse.ArgumentParser();p.add_argument("--diagnostic",type=Path,required=True);p.add_argument("--output",type=Path,required=True);p.add_argument("--split",required=True);a=p.parse_args();split=key(a.split)
    rows=[]
    with a.diagnostic.open(newline="",encoding="utf-8-sig") as f:
        for z in csv.DictReader(f): rows.append({"time":key(z["Time"]),"o":float(z["Open"]),"h":float(z["High"]),"l":float(z["Low"]),"c":float(z["Close"]),"range":float(z["RangeTicks"]),"d":int(z["TrendDirection"]),"s8":float(z["Slope8"]),"s24":float(z["Slope24"]),"g824":float(z["Separation8_24Ticks"]),"g2450":float(z["Separation24_50Ticks"]),"lo8":float(z["LowToEMA8Ticks"]),"hi8":float(z["HighToEMA8Ticks"]),"cl8":float(z["CloseToEMA8Ticks"]),"lo24":float(z["LowToEMA24Ticks"]),"hi24":float(z["HighToEMA24Ticks"]),"cl24":float(z["CloseToEMA24Ticks"]),"x8":z["CrossesEMA8"]=="True","complete":z["OutcomeComplete"]=="True","win":z["TargetStopResult"].strip()=="TARGET"})
    candidates={8:[],24:[]}
    for i,x in enumerate(rows):
        if i<12 or i+12>=len(rows) or x["range"]!=5 or not x["complete"] or not x["d"] or not trend(x,x["d"]):continue
        if x["g824"]<=0 or x["d"]*x["g2450"]<=0:continue
        d=x["d"];shape=bar_shape(x,d);p24=prior24(rows,i,d)
        n=length(rows,i,d,3);side=x["cl8"]>=0 if d>0 else x["cl8"]<=0
        if n and side and x["x8"]:
            pen=-x["lo8"] if d>0 else x["hi8"];ref=min(rows[i-1]["l"],rows[i-2]["l"]) if d>0 else max(rows[i-1]["h"],rows[i-2]["h"]);disp=(ref-x["l"])/TICK if d>0 else (x["h"]-ref)/TICK
            candidates[8].append((x["time"]<=split,x["win"],n,shape,p24,d*x["s8"],x["g824"],pen,disp,selected8(rows,i,d,n)))
        n=length(rows,i,d,4);side=x["cl24"]>=0 if d>0 else x["cl24"]<=0
        if n and side:
            deep=max((-rows[i-j]["lo24"] if d>0 else rows[i-j]["hi24"]) for j in range(n+1));rec=d*(x["c"]-rows[i-1]["c"])/TICK
            candidates[24].append((x["time"]<=split,x["win"],n,shape,p24,d*x["s24"],x["g824"],d*x["g2450"],deep,rec,selected24(rows,i,d,n)))
    out=[]
    for ema,items in candidates.items():
        if ema==8:
            configs=((n,s,p,c,g,lo,hi,disp) for n in (1,2,3) for s in ("no_tail","tail_1","tail_2_3","tail_ge4") for p in (20,30,39,45) for c in (0,15,30) for g in (3,5) for lo in (0,1) for hi in (2.5,4.5,6) for disp in (0,1))
        else:
            configs=((n,s,p,c,g1,g2,hi,rec) for n in (1,2,3,4) for s in ("no_tail","tail_1","tail_2_3","tail_ge4") for p in (20,30,39,45) for c in (0,10,15) for g1 in (1.5,3) for g2 in (1.5,3) for hi in (5,7) for rec in (0,1,2))
        for cfg in configs:
            totals=[[0,0],[0,0]]
            for z in items:
                early,win,n,shape=z[:4]
                if n!=cfg[0] or shape!=cfg[1] or z[-1]:continue
                if ema==8: ok=z[4]>=cfg[2] and z[5]>=cfg[3] and z[6]>=cfg[4] and cfg[5]<=z[7]<=cfg[6] and z[8]>=cfg[7]
                else: ok=z[4]>=cfg[2] and z[5]>=cfg[3] and z[6]>=cfg[4] and z[7]>=cfg[5] and 0<=z[8]<=cfg[6] and z[9]>=cfg[7]
                if ok:q=totals[0 if early else 1];q[0]+=1;q[1]+=win
            if totals[0][0] and totals[1][0]:out.append([ema,*cfg,*totals[0],rate(*totals[0]),*totals[1],rate(*totals[1])])
    with a.output.open("w",newline="") as f:
        w=csv.writer(f);w.writerow(["EMA","PullbackBars","Shape","Prior24Min","CurrentSlopeMin","Gap1Min","Gap2OrPenMin","PenMax","RecoveryOrDispMin","DevN","DevTarget","DevRate","HoldN","HoldTarget","HoldRate"]);w.writerows(out)
    for ema in (8,24):
        print("EMA",ema)
        eligible=[z for z in out if z[0]==ema and z[9]>=15 and z[12]>=15]
        for z in sorted(eligible,key=lambda q:(-min(q[11],q[14]),-(q[9]+q[12])))[:20]:print(",".join(map(str,z)))
if __name__=="__main__":main()
