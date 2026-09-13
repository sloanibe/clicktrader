#!/usr/bin/env python3
"""Walk-forward scan of interpretable filters for broad 8-EMA pullbacks."""
import argparse,csv,math
from datetime import date
from pathlib import Path
def key(s):
 d=date(int(s[:4]),int(s[5:7]),int(s[8:10]));return d.toordinal()*86400+int(s[11:13])*3600+int(s[14:16])*60+int(s[17:19])
def main():
 p=argparse.ArgumentParser();p.add_argument('--diagnostic',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--split',required=True);a=p.parse_args();split=key(a.split);r=[]
 with a.diagnostic.open(newline='',encoding='utf-8-sig')as f:
  for x in csv.DictReader(f):r.append({'time':key(x['Time']),'o':float(x['Open']),'h':float(x['High']),'l':float(x['Low']),'c':float(x['Close']),'range':float(x['RangeTicks']),'d':int(x['TrendDirection']),'s8':float(x['Slope8']),'s24':float(x['Slope24']),'s50':float(x['Slope50']),'g824':float(x['Separation8_24Ticks']),'g850':float(x['Separation8_50Ticks']),'g2450':float(x['Separation24_50Ticks']),'lo8':float(x['LowToEMA8Ticks']),'hi8':float(x['HighToEMA8Ticks']),'cl8':float(x['CloseToEMA8Ticks']),'x8':x['CrossesEMA8']=='True','complete':x['OutcomeComplete']=='True','out':x['TargetStopResult'].strip()})
 candidates=[]
 for i,x in enumerate(r):
  if i<8 or i+12>=len(r) or x['range']!=5 or not x['complete'] or x['d']==0:continue
  d=x['d']; pull=(r[i-1]['c']<r[i-1]['o'] and r[i-2]['c']<r[i-2]['o'] and r[i-3]['c']>r[i-3]['o'] and r[i-4]['c']>r[i-4]['o']) if d>0 else (r[i-1]['c']>r[i-1]['o'] and r[i-2]['c']>r[i-2]['o'] and r[i-3]['c']<r[i-3]['o'] and r[i-4]['c']<r[i-4]['o'])
  side=x['cl8']>=0 if d>0 else x['cl8']<=0;color=x['c']>=x['o'] if d>0 else x['c']<=x['o']
  ordered=x['g824']>0 and d*x['g850']>0 and d*x['g2450']>0
  if not(pull and x['x8'] and side and color and ordered):continue
  pen=-x['lo8'] if d>0 else x['hi8']; ref=min(r[i-1]['l'],r[i-2]['l']) if d>0 else max(r[i-1]['h'],r[i-2]['h']);disp=(ref-x['l'])/.25 if d>0 else (x['h']-ref)/.25
  candidates.append((x['time']<=split,x['out']=='TARGET',max(d*r[i-j]['s8'] for j in range(1,7)),max(d*r[i-j]['s24'] for j in range(1,7)),max(d*r[i-j]['s50'] for j in range(1,7)),d*x['s8'],x['g824'],d*x['g2450'],pen,disp))
 print('broad candidates',len(candidates))
 rows=[]
 for p8 in (20,30,39,45):
  for p24 in (10,20,30,39):
   for gap in (1.5,3,4,5):
    for penmin,penmax in ((0,4.5),(.5,4.5),(1,4.5),(1,2.5)):
     for current in (-999,0,15):
      groups=[[0,0],[0,0]]
      for c in candidates:
       early,win,b8,b24,b50,cur,g824,g2450,pen,disp=c
       if b8<p8 or b24<p24 or g824<gap or not(penmin<=pen<=penmax) or cur<current or disp<1:continue
       z=groups[0 if early else 1];z[0]+=1;z[1]+=win
      dr=100*groups[0][1]/groups[0][0] if groups[0][0] else 0;hr=100*groups[1][1]/groups[1][0] if groups[1][0] else 0
      rows.append([p8,p24,gap,penmin,penmax,current,*groups[0],round(dr,2),*groups[1],round(hr,2)])
 a.output.parent.mkdir(parents=True,exist_ok=True)
 with a.output.open('w',newline='')as f:
  w=csv.writer(f);w.writerow(['Prior8Min','Prior24Min','Gap824Min','PenMin','PenMax','Current8Min','DevN','DevTarget','DevRate','HoldN','HoldTarget','HoldRate']);w.writerows(rows)
 for z in sorted((x for x in rows if x[6]>=30 and x[9]>=30),key=lambda x:(-min(x[8],x[11]),-(x[6]+x[9])))[:25]:print(','.join(map(str,z)))
if __name__=='__main__':main()
