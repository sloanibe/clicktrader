#!/usr/bin/env python3
"""Tick-audit the exploratory 5/24/50 slow-EMA bounce template."""
import argparse,csv,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent))
from ema_bounce_walkforward import k,ema,setup,result
from tick_execution_audit import Candidate,DiagnosticRow,audit,write_results

def main():
 p=argparse.ArgumentParser();p.add_argument('--diagnostic',type=Path,required=True);p.add_argument('--ticks',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--from',dest='start',required=True);p.add_argument('--to',dest='end',required=True);p.add_argument('--fast',type=int,default=5);p.add_argument('--middle',type=int,default=24);p.add_argument('--slow',type=int,default=50);a=p.parse_args();start,end=k(a.start),k(a.end)
 rows=[]
 with a.diagnostic.open(newline='',encoding='utf-8-sig')as f:
  for x in csv.DictReader(f):rows.append({'bar':int(x['BarNumber']),'time':k(x['Time']),'stamp':x['Time'],'o':float(x['Open']),'h':float(x['High']),'l':float(x['Low']),'c':float(x['Close']),'range':float(x['RangeTicks'])})
 close=[x['c']for x in rows];fast,middle,slow=ema(close,a.fast),ema(close,a.middle),ema(close,a.slow);items=[]
 for i,x in enumerate(rows):
  d=setup(rows,fast,middle,slow,i,2)
  if not d or not(start<=x['time']<=end):continue
  o={1:'TARGET',0:'STOP',-1:'NONE'}[result(rows,i,d)];same=any(rows[i+j]['time']==x['time'] for j in range(1,13))
  row=DiagnosticRow(x['bar'],x['time'],x['stamp'],d,x['c'],12,o);items.append(Candidate(len(items),row,rows[i+12]['time'],same))
 print('loaded',len(items),'custom %d/%d/%d slow candidates' %(a.fast,a.middle,a.slow),flush=True)
 audit(items,a.ticks,start,end,5,10,.25);matrix=write_results(a.output,items);print('wrote',a.output)
 for z,n in sorted(matrix.items()):print(','.join(z)+','+str(n))
if __name__=='__main__':main()
