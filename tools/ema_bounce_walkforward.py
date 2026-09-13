#!/usr/bin/env python3
"""Broad, two-period EMA-bounce exploration using diagnostic range bars.

Tests bounce location (fast/middle/slow) across EMA triples. A setup requires
ordered averages, a two-bar countertrend pullback preceded by two trend bars,
touch/rejection of the selected EMA, and strong *recent* trend slope. Current
slope is deliberately not required, allowing a pullback to flatten the EMA.
"""
import argparse,csv,math
from datetime import date
from pathlib import Path

def k(s):
 d=date(int(s[:4]),int(s[5:7]),int(s[8:10]));return d.toordinal()*86400+int(s[11:13])*3600+int(s[14:16])*60+int(s[17:19])
def ema(x,n):
 a=2/(n+1);v=x[0];o=[]
 for z in x:v=a*z+(1-a)*v;o.append(v)
 return o
def sl(e,i):return math.degrees(math.atan2(e[i]-e[i-3],.75))
def result(r,i,d):
 q=r[i]['c']
 for x in r[i+1:i+13]:
  hi=(x['h']-q)/.25 if d>0 else (q-x['l'])/.25;lo=(x['l']-q)/.25 if d>0 else (q-x['h'])/.25
  if lo<=-10:return 0
  if hi>=5:return 1
 return -1
def setup(r,a,b,c,i,target):
 if i<12 or i+12>=len(r) or r[i]['range']!=5:return 0
 d=1 if a[i]>b[i] else -1 if a[i]<b[i] else 0
 if not d:return 0
 order=(a[i]>b[i]>c[i]) if d>0 else (a[i]<b[i]<c[i])
 if not order:return 0
 # Recent momentum, not current slope: the pullback may flatten its target EMA.
 best_a=max(d*sl(a,i-j) for j in range(7));best_b=max(d*sl(b,i-j) for j in range(7));best_c=max(d*sl(c,i-j) for j in range(7))
 if best_a<20 or best_b<20 or best_c<10:return 0
 pull=(r[i-1]['c']<r[i-1]['o'] and r[i-2]['c']<r[i-2]['o'] and r[i-3]['c']>r[i-3]['o'] and r[i-4]['c']>r[i-4]['o']) if d>0 else (r[i-1]['c']>r[i-1]['o'] and r[i-2]['c']>r[i-2]['o'] and r[i-3]['c']<r[i-3]['o'] and r[i-4]['c']<r[i-4]['o'])
 if not pull:return 0
 e=(a,b,c)[target][i];cross=r[i]['l']<=e<=r[i]['h'];close=(r[i]['c']>=e) if d>0 else (r[i]['c']<=e);color=(r[i]['c']>=r[i]['o']) if d>0 else (r[i]['c']<=r[i]['o'])
 # Cap penetration by location: shallow fast, normal middle, deeper slow.
 pen=(e-r[i]['l'])/.25 if d>0 else (r[i]['h']-e)/.25
 cap=(4.5,7.0,10.0)[target]
 return d if cross and 0<=pen<=cap and close and color else 0
def main():
 p=argparse.ArgumentParser();p.add_argument('--diagnostic',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--development-end',required=True);p.add_argument('--holdout-start',required=True);a=p.parse_args()
 with a.diagnostic.open(newline='',encoding='utf-8-sig') as f:r=[{'time':k(x['Time']),'o':float(x['Open']),'h':float(x['High']),'l':float(x['Low']),'c':float(x['Close']),'range':float(x['RangeTicks'])}for x in csv.DictReader(f)]
 vals=[x['c']for x in r];F=[5,8,11,13,16];M=[18,24,30,36,42];S=[40,50,60,75];E={n:ema(vals,n)for n in set(F+M+S)};de=k(a.development_end);ho=k(a.holdout_start);out=[]
 for f in F:
  for m in M:
   for s in S:
    if not f<m<s:continue
    for target,name in enumerate(('fast','middle','slow')):
     z=[[0,0,0],[0,0,0]]
     for i,x in enumerate(r):
      d=setup(r,E[f],E[m],E[s],i,target)
      if not d:continue
      part=0 if x['time']<=de else 1 if x['time']>=ho else -1
      if part>=0:
       q=result(r,i,d);z[part][0]+=1;z[part][1]+=q==1;z[part][2]+=q==0
     dr=100*z[0][1]/z[0][0] if z[0][0]else 0;hr=100*z[1][1]/z[1][0] if z[1][0]else 0
     out.append([f,m,s,name,*z[0],round(dr,2),*z[1],round(hr,2)])
 a.output.parent.mkdir(parents=True,exist_ok=True)
 with a.output.open('w',newline='')as f:
  w=csv.writer(f);w.writerow(['Fast','Middle','Slow','BounceEMA','DevN','DevTarget','DevStop','DevRate','HoldN','HoldTarget','HoldStop','HoldRate']);w.writerows(out)
 print('Fast,Middle,Slow,BounceEMA,DevN,DevRate,HoldN,HoldRate')
 for x in sorted(out,key=lambda z:(-z[11],-z[8]))[:30]:print(*x[:4],x[4],x[7],x[8],x[11],sep=',')
if __name__=='__main__':main()
