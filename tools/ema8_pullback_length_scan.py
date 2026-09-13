#!/usr/bin/env python3
"""Independent one/two/three-bar 8-EMA pullback comparison."""
import argparse,csv
from datetime import date
from pathlib import Path
def key(s):
 d=date(int(s[:4]),int(s[5:7]),int(s[8:10]));return d.toordinal()*86400+int(s[11:13])*3600+int(s[14:16])*60+int(s[17:19])
def main():
 p=argparse.ArgumentParser();p.add_argument('--diagnostic',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--split',required=True);a=p.parse_args();split=key(a.split);r=[]
 with a.diagnostic.open(newline='',encoding='utf-8-sig')as f:
  for x in csv.DictReader(f):r.append({'time':key(x['Time']),'o':float(x['Open']),'h':float(x['High']),'l':float(x['Low']),'c':float(x['Close']),'range':float(x['RangeTicks']),'d':int(x['TrendDirection']),'s8':float(x['Slope8']),'s24':float(x['Slope24']),'g824':float(x['Separation8_24Ticks']),'g850':float(x['Separation8_50Ticks']),'g2450':float(x['Separation24_50Ticks']),'lo8':float(x['LowToEMA8Ticks']),'hi8':float(x['HighToEMA8Ticks']),'cl8':float(x['CloseToEMA8Ticks']),'x8':x['CrossesEMA8']=='True','complete':x['OutcomeComplete']=='True','win':x['TargetStopResult'].strip()=='TARGET'})
 base={1:[],2:[],3:[]}
 for i,x in enumerate(r):
  if i<10 or i+12>=len(r) or x['range']!=5 or not x['complete'] or not x['d']:continue
  d=x['d'];side=x['cl8']>=0 if d>0 else x['cl8']<=0;color=x['c']>=x['o'] if d>0 else x['c']<=x['o'];ordered=x['g824']>0 and d*x['g850']>0 and d*x['g2450']>0
  if not(x['x8'] and side and color and ordered):continue
  pen=-x['lo8'] if d>0 else x['hi8'];ref=min(r[i-1]['l'],r[i-2]['l']) if d>0 else max(r[i-1]['h'],r[i-2]['h']);disp=(ref-x['l'])/.25 if d>0 else (x['h']-ref)/.25;prior8=max(d*r[i-j]['s8'] for j in range(1,7));prior24=max(d*r[i-j]['s24'] for j in range(1,7));cur8=d*x['s8']
  for n in base:
   counter=all((r[i-j]['c']<r[i-j]['o']) if d>0 else (r[i-j]['c']>r[i-j]['o']) for j in range(1,n+1));preceding=(r[i-n-1]['c']>=r[i-n-1]['o']) if d>0 else (r[i-n-1]['c']<=r[i-n-1]['o'])
   if counter and preceding:base[n].append((x['time']<=split,x['win'],prior8,prior24,cur8,x['g824'],pen,disp))
 rows=[]
 for n,items in base.items():
  print('pullback_bars=%d base_candidates=%d'%(n,len(items)))
  for b8 in (0,20,39):
   for b24 in (10,20,30,39):
    for gap in (1.5,3,5):
     for lo,hi in ((0,4.5),(.5,4.5),(1,4.5),(1,2.5)):
      for cur in (-999,0,15):
       z=[[0,0],[0,0]]
       for x in items:
        early,win,p8,p24,c8,g,pen,disp=x
        if p8<b8 or p24<b24 or c8<cur or g<gap or not(lo<=pen<=hi) or disp<1:continue
        q=z[0 if early else 1];q[0]+=1;q[1]+=win
       dr=100*z[0][1]/z[0][0] if z[0][0]else 0;hr=100*z[1][1]/z[1][0] if z[1][0]else 0;rows.append([n,b8,b24,gap,lo,hi,cur,*z[0],round(dr,2),*z[1],round(hr,2)])
 a.output.parent.mkdir(parents=True,exist_ok=True)
 with a.output.open('w',newline='')as f:
  w=csv.writer(f);w.writerow(['PullbackBars','Prior8Min','Prior24Min','GapMin','PenMin','PenMax','Current8Min','DevN','DevTarget','DevRate','HoldN','HoldTarget','HoldRate']);w.writerows(rows)
 for n in (1,2,3):
  print('TOP',n)
  q=[x for x in rows if x[0]==n and x[7]>=50 and x[10]>=50]
  for x in sorted(q,key=lambda x:(-min(x[9],x[12]),-(x[7]+x[10])))[:8]:print(','.join(map(str,x)))
if __name__=='__main__':main()
