#!/usr/bin/env python3
"""Conservative daily simulation: slow primary, any EMA-bounce recovery pool."""
import argparse,csv,sys
from datetime import datetime,timedelta
from pathlib import Path
sys.path.insert(0,str(Path(__file__).parent))
from ema_bounce_walkforward import k,ema,setup,result

def session(stamp):
 d=datetime.strptime(stamp[:19],'%Y-%m-%d %H:%M:%S')
 return (d.date() if d.hour>=15 else (d-timedelta(days=1)).date()).isoformat()
def main():
 p=argparse.ArgumentParser();p.add_argument('--diagnostic',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--from',dest='start',required=True);p.add_argument('--to',dest='end',required=True);p.add_argument('--max-recovery',type=int,default=4);p.add_argument('--fast',type=int,default=5);p.add_argument('--middle',type=int,default=24);p.add_argument('--slow',type=int,default=50);a=p.parse_args();start,end=k(a.start),k(a.end)
 r=[]
 with a.diagnostic.open(newline='',encoding='utf-8-sig')as f:
  for x in csv.DictReader(f):r.append({'time':k(x['Time']),'stamp':x['Time'],'o':float(x['Open']),'h':float(x['High']),'l':float(x['Low']),'c':float(x['Close']),'range':float(x['RangeTicks'])})
 close=[x['c']for x in r];E=[ema(close,n)for n in(a.fast,a.middle,a.slow)];days={}
 for i,x in enumerate(r):
  if not(start<=x['time']<=end):continue
  for target,name in enumerate(('fast','middle','slow')):
   d=setup(r,*E,i,target)
   if d:days.setdefault(session(x['stamp']),[]).append((i,target,name,result(r,i,d)))
 a.output.parent.mkdir(parents=True,exist_ok=True);summary=[]
 with a.output.open('w',newline='')as f:
  w=csv.writer(f);w.writerow(['Session','PrimaryOutcome','RecoveryTrades','FinalTicks','Recovered','Sequence'])
  for day,signals in sorted(days.items()):
   signals.sort();primary=next((x for x in signals if x[2]=='slow'),None)
   if not primary:continue
   pnl=5 if primary[3]==1 else -10 if primary[3]==0 else 0;lock=primary[0]+12;seq=['slow:'+str(primary[3])];n=0
   while pnl<5 and n<a.max_recovery:
    nxt=next((x for x in signals if x[0]>=lock),None)
    if not nxt:break
    pnl += 5 if nxt[3]==1 else -10 if nxt[3]==0 else 0;lock=nxt[0]+12;n+=1;seq.append(nxt[2]+':'+str(nxt[3]))
   w.writerow([day,primary[3],n,pnl,pnl>=5,' '.join(seq)]);summary.append((primary[3],n,pnl))
 firstloss=[x for x in summary if x[0]==0];recover=sum(x[2]>=5 for x in firstloss)
 print('sessions=%d first_losses=%d recovered=%d recovery_rate=%.2f%% avg_recovery_trades=%.2f' %(len(summary),len(firstloss),recover,100*recover/len(firstloss) if firstloss else 0,sum(x[1] for x in firstloss)/len(firstloss) if firstloss else 0))
if __name__=='__main__':main()
