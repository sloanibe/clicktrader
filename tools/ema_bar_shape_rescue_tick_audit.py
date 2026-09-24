#!/usr/bin/env python3
"""Raw-tick audit of bar-shape EMA bounce expansion candidates."""
import argparse
import csv
import sys
from datetime import date
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from tick_execution_audit import Candidate, DiagnosticRow, audit, write_results

TICK = .25


def key(value):
    day = date(int(value[:4]), int(value[5:7]), int(value[8:10]))
    return day.toordinal()*86400 + int(value[11:13])*3600 + int(value[14:16])*60 + int(value[17:19])


def trend(row, direction):
    return float(row["Close"]) >= float(row["Open"]) if direction > 0 else float(row["Close"]) <= float(row["Open"])


def counter(row, direction):
    return float(row["Close"]) < float(row["Open"]) if direction > 0 else float(row["Close"]) > float(row["Open"])


def pullback(rows, i, direction, maximum):
    for n in range(1, maximum+1):
        if all(counter(rows[i-j], direction) for j in range(1,n+1)) and trend(rows[i-n-1], direction): return n
    return 0


def best24(rows, i, direction):
    return max(direction*float(rows[i-j]["Slope24"]) for j in range(1,7))


def selected8(rows, i, direction, n):
    if n not in (1,2): return False
    x=rows[i];pen=-float(x["LowToEMA8Ticks"]) if direction>0 else float(x["HighToEMA8Ticks"]);ref=min(float(rows[i-1]["Low"]),float(rows[i-2]["Low"])) if direction>0 else max(float(rows[i-1]["High"]),float(rows[i-2]["High"]));disp=(ref-float(x["Low"]))/TICK if direction>0 else (float(x["High"])-ref)/TICK
    return (float(x["Separation8_24Ticks"])>=5 and direction*float(x["Slope8"])>=15 and best24(rows,i,direction)>=(20 if n==1 else 39) and 1<=pen<=(4.5 if n==1 else 2.5) and disp>=1)


def selected24(rows, i, direction, n):
    if n not in (1,2,3): return False
    cfg={1:(45,10,3,3,0),2:(30,15,1.5,3,2),3:(39,0,1.5,1.5,1)}[n];x=rows[i];deep=max((-float(rows[i-j]["LowToEMA24Ticks"]) if direction>0 else float(rows[i-j]["HighToEMA24Ticks"])) for j in range(n+1));rec=direction*(float(x["Close"])-float(rows[i-1]["Close"]))/TICK
    return best24(rows,i,direction)>=cfg[0] and direction*float(x["Slope24"])>=cfg[1] and float(x["Separation8_24Ticks"])>=cfg[2] and direction*float(x["Separation24_50Ticks"])>=cfg[3] and rec>=cfg[4] and 0<=deep<=5


def main():
    p=argparse.ArgumentParser();p.add_argument("--diagnostic",type=Path,required=True);p.add_argument("--ticks",type=Path,required=True);p.add_argument("--output",type=Path,required=True);p.add_argument("--from",dest="start",required=True);p.add_argument("--to",dest="end",required=True);a=p.parse_args();start,end=key(a.start),key(a.end)
    with a.diagnostic.open(newline="",encoding="utf-8-sig") as f:rows=list(csv.DictReader(f))
    items=[];rules={}
    for i,x in enumerate(rows):
        if i<12 or i+12>=len(rows) or not start<=key(x["Time"])<=end or x["OutcomeComplete"]!="True" or float(x["RangeTicks"])!=5:continue
        d=int(x["TrendDirection"])
        if not d or not trend(x,d):continue
        if float(x["Separation8_24Ticks"])<=0 or d*float(x["Separation24_50Ticks"])<=0:continue
        tail=(float(x["Open"])-float(x["Low"]))/TICK if d>0 else (float(x["High"])-float(x["Open"]))/TICK
        rule="";n=pullback(rows,i,d,3);side=float(x["CloseToEMA8Ticks"])>=0 if d>0 else float(x["CloseToEMA8Ticks"])<=0
        if n and x["CrossesEMA8"]=="True" and side and not selected8(rows,i,d,n):
            pen=-float(x["LowToEMA8Ticks"]) if d>0 else float(x["HighToEMA8Ticks"]);ref=min(float(rows[i-1]["Low"]),float(rows[i-2]["Low"])) if d>0 else max(float(rows[i-1]["High"]),float(rows[i-2]["High"]));disp=(ref-float(x["Low"]))/TICK if d>0 else (float(x["High"])-ref)/TICK;p24=best24(rows,i,d);c8=d*float(x["Slope8"]);gap=float(x["Separation8_24Ticks"])
            if n==1 and tail<=.1 and p24>=45 and c8>=0 and gap>=3 and 0<=pen<=4.5:rule="8_NO_TAIL_1"
            elif n==1 and abs(tail-1)<.1 and p24>=20 and c8>=15 and gap>=5 and 1<=pen<=2.5:rule="8_TAIL1_1"
            elif n==1 and tail>=4 and p24>=20 and c8>=15 and gap>=3 and 0<=pen<=2.5 and disp>=1:rule="8_LONG_TAIL_1"
            elif n==3 and tail<=.1 and p24>=20 and c8>=0 and gap>=3 and 0<=pen<=4.5 and disp>=1:rule="8_NO_TAIL_3"
        if not rule:
            n=pullback(rows,i,d,4);side=float(x["CloseToEMA24Ticks"])>=0 if d>0 else float(x["CloseToEMA24Ticks"])<=0
            if n and side and not selected24(rows,i,d,n):
                deep=max((-float(rows[i-j]["LowToEMA24Ticks"]) if d>0 else float(rows[i-j]["HighToEMA24Ticks"])) for j in range(n+1));rec=d*(float(x["Close"])-float(rows[i-1]["Close"]))/TICK
                p24=best24(rows,i,d);c24=d*float(x["Slope24"]);gap824=float(x["Separation8_24Ticks"]);gap2450=d*float(x["Separation24_50Ticks"])
                if n==2 and abs(tail-1)<.1 and p24>=30 and c24>=10 and gap824>=3 and gap2450>=1.5 and rec>=2 and 0<=deep<=5:rule="24_TAIL1_2"
                elif n==4 and tail<=.1 and p24>=39 and c24>=0 and gap824>=1.5 and gap2450>=1.5 and rec>=2 and 0<=deep<=5:rule="24_NO_TAIL_4"
        if not rule:continue
        row=DiagnosticRow(int(x["BarNumber"]),key(x["Time"]),x["Time"],d,float(x["Close"]),12,x["TargetStopResult"].strip());same=any(key(rows[i+j]["Time"])==key(x["Time"]) for j in range(1,13));candidate=Candidate(len(items),row,key(rows[i+12]["Time"]),same);items.append(candidate);rules[candidate.candidate_id]=rule
    print("loaded",len(items),"bar-shape rescue candidates",flush=True);audit(items,a.ticks,start,end,5,10,TICK);write_results(a.output,items)
    with a.output.with_name(a.output.stem+"_rules.csv").open("w",newline="") as f:w=csv.writer(f);w.writerow(["CandidateIndex","Rule"]);w.writerows(sorted(rules.items()))
if __name__=="__main__":main()
