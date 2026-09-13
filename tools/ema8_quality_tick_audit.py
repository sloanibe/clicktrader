#!/usr/bin/env python3
"""Tick audit the selected broad 8-EMA quality tier."""
import argparse,csv,sys
from datetime import date
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent))
from tick_execution_audit import Candidate,DiagnosticRow,audit,write_results
def k(s):
 d=date(int(s[:4]),int(s[5:7]),int(s[8:10]));return d.toordinal()*86400+int(s[11:13])*3600+int(s[14:16])*60+int(s[17:19])
def main():
 p=argparse.ArgumentParser();p.add_argument('--diagnostic',type=Path,required=True);p.add_argument('--ticks',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--from',dest='start',required=True);p.add_argument('--to',dest='end',required=True);p.add_argument('--prior24',type=float,default=39);p.add_argument('--gap',type=float,default=5);p.add_argument('--pen-min',type=float,default=1);p.add_argument('--pen-max',type=float,default=2.5);p.add_argument('--pullback-bars',type=int,default=2);p.add_argument('--current8',type=float,default=-999);a=p.parse_args();start,end=k(a.start),k(a.end);r=[]
 with a.diagnostic.open(newline='',encoding='utf-8-sig')as f:
  for x in csv.DictReader(f):r.append(x)
 items=[]
 for i,x in enumerate(r):
  if i<8 or i+12>=len(r) or not(start<=k(x['Time'])<=end) or x['OutcomeComplete']!='True' or float(x['RangeTicks'])!=5:continue
  d=int(x['TrendDirection']);
  if not d:continue
  def color(j,sign):return float(r[j]['Close'])>=float(r[j]['Open']) if sign>0 else float(r[j]['Close'])<=float(r[j]['Open'])
  pull=all(not color(i-j,d) for j in range(1,a.pullback_bars+1)) and color(i-a.pullback_bars-1,d);side=float(x['CloseToEMA8Ticks'])>=0 if d>0 else float(x['CloseToEMA8Ticks'])<=0;ordered=float(x['Separation8_24Ticks'])>0 and d*float(x['Separation8_50Ticks'])>0 and d*float(x['Separation24_50Ticks'])>0;pen=-float(x['LowToEMA8Ticks']) if d>0 else float(x['HighToEMA8Ticks']);prior24=max(d*float(r[i-j]['Slope24']) for j in range(1,7));cur8=d*float(x['Slope8']);ref=min(float(r[i-1]['Low']),float(r[i-2]['Low'])) if d>0 else max(float(r[i-1]['High']),float(r[i-2]['High']));disp=(ref-float(x['Low']))/.25 if d>0 else (float(x['High'])-ref)/.25
  if not(pull and x['CrossesEMA8']=='True' and side and color(i,d) and ordered and prior24>=a.prior24 and cur8>=a.current8 and float(x['Separation8_24Ticks'])>=a.gap and a.pen_min<=pen<=a.pen_max and disp>=1):continue
  row=DiagnosticRow(int(x['BarNumber']),k(x['Time']),x['Time'],d,float(x['Close']),12,x['TargetStopResult'].strip());same=any(k(r[i+j]['Time'])==k(x['Time']) for j in range(1,13));items.append(Candidate(len(items),row,k(r[i+12]['Time']),same))
 print('loaded',len(items),'quality EMA8 candidates',flush=True);audit(items,a.ticks,start,end,5,10,.25);write_results(a.output,items)
if __name__=='__main__':main()
