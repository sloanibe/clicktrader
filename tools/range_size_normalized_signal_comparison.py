#!/usr/bin/env python3
"""Compare current EMA signal logic normalized across range-bar sizes."""
from __future__ import annotations

import argparse
import csv
import math
from collections import Counter
from datetime import datetime
from pathlib import Path


TICK = .25
SPLIT = "2026-08-23 23:59:59"


def f(row, key):
    return float(row[key])


def angle(current, previous, bars=3):
    return math.degrees(math.atan2(current - previous, bars * TICK))


def slope_threshold(degrees, scale):
    return math.degrees(math.atan(scale * math.tan(math.radians(degrees))))


def current_slope(rows, index, ema, side):
    return side * angle(f(rows[index], ema), f(rows[index-3], ema))


def best_slope(rows, index, ema, side, lookback):
    return max(side * angle(f(rows[index-j], ema), f(rows[index-j-3], ema))
               for j in range(1, lookback + 1))


def trend(row, side):
    return f(row, "Close") >= f(row, "Open") if side > 0 else f(row, "Close") <= f(row, "Open")


def counter(row, side):
    return f(row, "Close") < f(row, "Open") if side > 0 else f(row, "Close") > f(row, "Open")


def pullback(rows, index, side, maximum):
    for length in range(1, maximum + 1):
        if (all(counter(rows[index-j], side) for j in range(1, length+1)) and
                trend(rows[index-length-1], side)):
            return length
    return 0


def ordered(row):
    e8, e24, e50 = f(row, "EMA8"), f(row, "EMA24"), f(row, "EMA50")
    if e8 > e24 > e50:
        return 1
    if e8 < e24 < e50:
        return -1
    return 0


def prime(timestamp):
    value = datetime.fromisoformat(timestamp)
    seconds = value.hour * 3600 + value.minute * 60 + value.second
    return ((6*3600 + 31*60) <= seconds < (6*3600 + 45*60) or
            7*3600 <= seconds < 8*3600 or 11*3600 <= seconds < 13*3600)


def quality8(rows, i, side, scale):
    row = rows[i]; length = pullback(rows, i, side, 3)
    if not length or not trend(row, side): return False
    e8 = f(row, "EMA8")
    if not f(row, "Low") <= e8 <= f(row, "High"): return False
    if f(row, "Close") < e8 if side > 0 else f(row, "Close") > e8: return False
    sep = abs(e8 - f(row, "EMA24")) / TICK
    prior24 = best_slope(rows, i, "EMA24", side, 6)
    slope8 = current_slope(rows, i, "EMA8", side)
    pen = ((e8-f(row,"Low"))/TICK if side>0 else (f(row,"High")-e8)/TICK)
    ref = (min(f(rows[i-1],"Low"),f(rows[i-2],"Low")) if side>0
           else max(f(rows[i-1],"High"),f(rows[i-2],"High")))
    disp = ((ref-f(row,"Low"))/TICK if side>0 else (f(row,"High")-ref)/TICK)
    tail = ((f(row,"Open")-f(row,"Low"))/TICK if side>0
            else (f(row,"High")-f(row,"Open"))/TICK)
    base = (length in (1,2) and sep >= 5*scale and
            slope8 >= slope_threshold(15,scale) and
            prior24 >= slope_threshold(20 if length==1 else 39,scale) and
            1*scale <= pen <= (4.5 if length==1 else 2.5)*scale and
            disp >= 1*scale)
    return (base or
            (length==1 and tail<=.1 and prior24>=slope_threshold(45,scale) and
             slope8>=0 and sep>=3*scale and 0<=pen<=4.5*scale) or
            (length==1 and abs(tail-1)<=.1 and prior24>=slope_threshold(20,scale) and
             slope8>=slope_threshold(15,scale) and sep>=5*scale and
             1*scale<=pen<=2.5*scale) or
            (length==1 and tail>=.8*(5*scale) and prior24>=slope_threshold(20,scale) and
             slope8>=slope_threshold(15,scale) and sep>=3*scale and
             0<=pen<=2.5*scale and disp>=1*scale) or
            (length==3 and tail<=.1 and prior24>=slope_threshold(20,scale) and
             slope8>=0 and sep>=3*scale and 0<=pen<=4.5*scale and disp>=1*scale))


def quality24(rows, i, side, scale):
    row=rows[i];length=pullback(rows,i,side,4)
    if not length or not trend(row,side):return False
    e24=f(row,"EMA24")
    if f(row,"Close")<e24 if side>0 else f(row,"Close")>e24:return False
    prior=best_slope(rows,i,"EMA24",side,6);cur=current_slope(rows,i,"EMA24",side)
    g8=abs(f(row,"EMA8")-e24)/TICK;g50=abs(e24-f(row,"EMA50"))/TICK
    rec=side*(f(row,"Close")-f(rows[i-1],"Close"))/TICK
    deep=max(((f(rows[i-j],"EMA24")-f(rows[i-j],"Low"))/TICK if side>0
              else (f(rows[i-j],"High")-f(rows[i-j],"EMA24"))/TICK)
             for j in range(length+1))
    if not 0<=deep<=5*scale:return False
    tail=((f(row,"Open")-f(row,"Low"))/TICK if side>0 else (f(row,"High")-f(row,"Open"))/TICK)
    base=((length==1 and prior>=slope_threshold(45,scale) and cur>=slope_threshold(10,scale) and g8>=3*scale and g50>=3*scale and rec>=0) or
          (length==2 and prior>=slope_threshold(30,scale) and cur>=slope_threshold(15,scale) and g8>=1.5*scale and g50>=3*scale and rec>=2*scale) or
          (length==3 and prior>=slope_threshold(39,scale) and cur>=0 and g8>=1.5*scale and g50>=1.5*scale and rec>=1*scale))
    return (base or
            (length==2 and abs(tail-1)<=.1 and prior>=slope_threshold(30,scale) and cur>=slope_threshold(10,scale) and g8>=3*scale and g50>=1.5*scale and rec>=2*scale) or
            (length==2 and prior>=slope_threshold(30,scale) and cur>=slope_threshold(15,scale) and g8>=1.5*scale and g50>=3*scale and rec<2*scale and deep<=1*scale) or
            (length==4 and tail<=.1 and prior>=slope_threshold(39,scale) and cur>=0 and g8>=1.5*scale and g50>=1.5*scale and rec>=2*scale))


def quality50(rows,i,side,scale):
    row=rows[i]
    if not trend(row,side):return False
    e50=f(row,"EMA50")
    if f(row,"Close")<e50 if side>0 else f(row,"Close")>e50:return False
    length=pullback(rows,i,side,3)
    if not length:return False
    pre=rows[i-length-1]
    if not (side*(f(pre,"EMA8")-f(pre,"EMA24"))>0 and side*(f(pre,"EMA24")-f(pre,"EMA50"))>0):return False
    p50=best_slope(rows,i,"EMA50",side,8);p24=best_slope(rows,i,"EMA24",side,8)
    c50=current_slope(rows,i,"EMA50",side);gap=side*(f(row,"EMA24")-e50)/TICK
    if gap<.001:return False
    rec=side*(f(row,"Close")-f(rows[i-1],"Close"))/TICK
    deep=max(((f(rows[i-j],"EMA50")-f(rows[i-j],"Low"))/TICK if side>0
              else (f(rows[i-j],"High")-f(rows[i-j],"EMA50"))/TICK)
             for j in range(length+1))
    tail=((f(row,"Open")-f(row,"Low"))/TICK if side>0 else (f(row,"High")-f(row,"Open"))/TICK)
    midtail=.4*(5*scale)<=tail<=.6*(5*scale)
    return ((length==1 and tail<=.1 and p50>=slope_threshold(10,scale) and c50>=slope_threshold(-10,scale) and gap>=1.5*scale and -1*scale<=deep<=5*scale and rec>=0) or
            (length==1 and midtail and p50>=slope_threshold(20,scale) and c50>=slope_threshold(10,scale) and gap>=0 and -1*scale<=deep<=5*scale and rec>=0) or
            (length==2 and midtail and p50>=slope_threshold(20,scale) and c50>=slope_threshold(10,scale) and gap>=0 and -1*scale<=deep<=5*scale and rec>=2*scale) or
            (length==3 and p50>=slope_threshold(20,scale) and p24>=slope_threshold(30,scale) and c50>=0 and gap>=1.5*scale and 0<=deep<=5*scale and rec>=2*scale) or
            (length==1 and p50>=slope_threshold(10,scale) and c50>=slope_threshold(10,scale) and gap>=3*scale and 0<=deep<=5*scale and rec>=0))


def pin_shape(rows,i,side,scale,tail_min):
    row=rows[i]
    if not trend(row,side):return None
    tail=((f(row,"Open")-f(row,"Low"))/TICK if side>0 else (f(row,"High")-f(row,"Open"))/TICK)
    if not tail_min<=tail<=5*scale+.1:return None
    e8=f(row,"EMA8");body=min(f(row,"Open"),f(row,"Close")) if side>0 else max(f(row,"Open"),f(row,"Close"))
    extreme=f(row,"Low") if side>0 else f(row,"High")
    if side*(body-e8)/TICK<=0:return None
    return tail,body,side*(extreme-e8)/TICK


def strict_pin(rows,i,side,scale,tail_min):
    shape=pin_shape(rows,i,side,scale,tail_min)
    if shape is None:return False
    _,_,distance=shape;row=rows[i]
    return (current_slope(rows,i,"EMA8",side)>=slope_threshold(60,scale) and
            current_slope(rows,i,"EMA24",side)>=slope_threshold(45,scale) and
            current_slope(rows,i,"EMA50",side)>=slope_threshold(39,scale) and
            abs(f(row,"EMA8")-f(row,"EMA24"))/TICK>=1.5*scale and
            abs(f(row,"EMA24")-f(row,"EMA50"))/TICK>=3*scale and
            distance>=4*scale and side*(f(row,"Close")-f(rows[i-1],"Close"))/TICK>=2*scale)


def pin7(rows,i,side,scale,tail_min):
    shape=pin_shape(rows,i,side,scale,tail_min)
    if shape is None:return False
    _,body,distance=shape
    if not 0<=distance<4*scale:return False
    return all((f(rows[i-j],"High")<body if side>0 else f(rows[i-j],"Low")>body)
               for j in range(1,8))


def path_outcome(rows,i,side,target,stop):
    entry=f(rows[i],"Close");target_price=entry+side*target*TICK;stop_price=entry-side*stop*TICK
    for future in rows[i+1:i+13]:
        hit_t=f(future,"High")>=target_price if side>0 else f(future,"Low")<=target_price
        hit_s=f(future,"Low")<=stop_price if side>0 else f(future,"High")>=stop_price
        if hit_s:return "STOP"
        if hit_t:return "TARGET"
    return "NONE"


def scan(path,bar_size,start,end,tail_min):
    with path.open(newline="",encoding="utf-8-sig") as source:rows=list(csv.DictReader(source))
    scale=bar_size/5;signals=[]
    for i,row in enumerate(rows):
        if i<20 or i+12>=len(rows) or not start<=row["Time"]<=end or not prime(row["Time"]) or row["OutcomeComplete"]!="True" or abs(f(row,"RangeTicks")-bar_size)>.001:continue
        families=[];side=ordered(row)
        if side:
            if quality8(rows,i,side,scale):families.append("EMA8")
            if quality24(rows,i,side,scale):families.append("EMA24")
            if strict_pin(rows,i,side,scale,tail_min):families.append("PIN")
            if pin7(rows,i,side,scale,tail_min):families.append("PIN7")
        side50=1 if f(row,"EMA24")>f(row,"EMA50") else -1
        if quality50(rows,i,side50,scale):families.append("EMA50")
        if not families:continue
        chosen_side=side if any(x in families for x in ("EMA8","EMA24","PIN","PIN7")) else side50
        signals.append({"bar":int(row["BarNumber"]),"time":row["Time"],"direction":chosen_side,"entry":f(row,"Close"),"families":"+".join(families),"segment":"DEV" if row["Time"]<=SPLIT else "HOLD","outcome":path_outcome(rows,i,chosen_side,bar_size,2*bar_size)})
    return signals


def summary(name,signals):
    counts=Counter(x["outcome"] for x in signals);resolved=counts["TARGET"]+counts["STOP"]
    def rate(items):
        c=Counter(x["outcome"] for x in items);n=c["TARGET"]+c["STOP"];return 100*c["TARGET"]/n if n else 0
    dev=[x for x in signals if x["segment"]=="DEV"];hold=[x for x in signals if x["segment"]=="HOLD"]
    return [name,len(signals),resolved,counts["TARGET"],counts["STOP"],counts["NONE"],rate(signals),len(dev),rate(dev),len(hold),rate(hold)]


def main():
    p=argparse.ArgumentParser();p.add_argument("--four",type=Path,required=True);p.add_argument("--five",type=Path,required=True);p.add_argument("--output",type=Path,required=True);p.add_argument("--from",dest="start",required=True);p.add_argument("--to",dest="end",required=True);a=p.parse_args()
    sets={"R4_TAIL3_4":scan(a.four,4,a.start,a.end,3),"R4_TAIL2_4":scan(a.four,4,a.start,a.end,2),"R5_CURRENT":scan(a.five,5,a.start,a.end,3)}
    a.output.parent.mkdir(parents=True,exist_ok=True)
    with a.output.open("w",newline="") as target:
        w=csv.writer(target);w.writerow(["Configuration","Selected","Resolved","Target","Stop","None","Rate","DevN","DevRate","HoldN","HoldRate"]);w.writerows(summary(name,items) for name,items in sets.items())
    for name,items in sets.items():
        detail=a.output.with_name(a.output.stem+"_"+name.lower()+".csv")
        with detail.open("w",newline="") as target:
            w=csv.DictWriter(target,fieldnames=list(items[0]));w.writeheader();w.writerows(items)
        print(summary(name,items))


if __name__=="__main__":main()
