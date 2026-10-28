import random, math
from PIL import Image, ImageDraw, ImageFont, ImageFilter
import glob
fs=glob.glob('/usr/share/fonts/**/*Bold*.ttf',recursive=True)+glob.glob('/usr/share/fonts/**/*.ttf',recursive=True)
FONT=fs[0] if fs else None
def font(n): return ImageFont.truetype(FONT,n) if FONT else ImageFont.load_default()
def bg(w,h,c1,c2,seed):
    random.seed(seed)
    im=Image.new('RGB',(w,h)); d=ImageDraw.Draw(im)
    for y in range(h):
        t=y/h; d.line([(0,y),(w,y)],fill=tuple(int(c1[i]*(1-t)+c2[i]*t) for i in range(3)))
    for _ in range(w*h//1500):
        x,y=random.randrange(w),random.randrange(h); r=random.choice([1,1,2])
        d.ellipse([x,y,x+r,y+r],fill=(255,255,255))
    return im
def glow(im,pos,r,col):
    l=Image.new('RGB',im.size,(0,0,0)); d=ImageDraw.Draw(l)
    d.ellipse([pos[0]-r,pos[1]-r,pos[0]+r,pos[1]+r],fill=col)
    l=l.filter(ImageFilter.GaussianBlur(r/2))
    return Image.blend(im,Image.composite(l,im,l.convert('L')),0.0) if False else Image.fromarray(__import__('numpy').clip(__import__('numpy').array(im,dtype=int)+__import__('numpy').array(l,dtype=int),0,255).astype('uint8'))
def title(im,text,sub,col,y):
    d=ImageDraw.Draw(im); w,h=im.size
    for s,f,c,dy in ((text,font(h//7),col,y),(sub,font(h//18),(230,225,210),y+h//7+10)):
        bb=d.textbbox((0,0),s,font=f); x=(w-bb[2])//2
        d.text((x+3,dy+3),s,font=f,fill=(0,0,0)); d.text((x,dy),s,font=f,fill=c)
def seed(w,h):
    im=bg(w,h,(18,14,40),(60,25,70),1); im=glow(im,(w//2,h//2),h//3,(90,60,20))
    d=ImageDraw.Draw(im); cx,cy=w//2,int(h*.34); R=h//6
    # magnifier
    d.ellipse([cx-R,cy-R,cx+R,cy+R],outline=(240,190,80),width=h//40,fill=(30,30,70))
    d.line([(cx+R*.7,cy+R*.7),(cx+R*1.6,cy+R*1.6)],fill=(240,190,80),width=h//18)
    # seed sprout + map nodes inside
    random.seed(5); pts=[(cx+random.randint(-R//2,R//2),cy+i*R//4-R//2) for i in range(5)]
    for a,b in zip(pts,pts[1:]): d.line([a,b],fill=(120,200,255),width=3)
    for p in pts: d.ellipse([p[0]-9,p[1]-9,p[0]+9,p[1]+9],fill=(255,120,90),outline=(255,255,255),width=2)
    for i in range(0):
        x=random.randint(30,w-30); y=random.randint(h//2+R,h-80)
        d.text((x,y),str(random.randint(0,9)),font=font(h//14),fill=(150,150,200))
    return im
def card(w,h):
    im=bg(w,h,(40,12,12),(15,18,45),2); im=glow(im,(w//2,h//2),h//3,(110,40,10))
    d=ImageDraw.Draw(im); cw,ch=h//5,int(h//5*1.4)
    for k,(ang,c) in enumerate(((-18,(150,150,160)),(0,(240,190,70)),(18,(70,150,240)))):
        l=Image.new('RGBA',(cw+8,ch+8),(0,0,0,0)); dd=ImageDraw.Draw(l)
        dd.rounded_rectangle([4,4,cw+4,ch+4],12,fill=(30,25,35,255),outline=c,width=5)
        dd.ellipse([cw//2-cw//4,ch//5,cw//2+cw//4+8,ch//5+cw//2],fill=c+(255,))
        dd.ellipse([cw//2-8,ch-40,cw//2+16,ch-16],fill=(255,255,255,255))
        l=l.rotate(ang,expand=True,resample=Image.BICUBIC)
        im.paste(l,(int(w//2-l.width//2+(k-1)*cw*0.95),int(h*.32)-l.height//2+abs(k-1)*-0+ (18 if k!=1 else 0)),l)
    return im
def save(im,name,t,s,col):
    title(im,t,s,col,int(im.size[1]*.62)); im.save(name)
import sys
for kind,fn,t,s,col in (('seed',seed,'Seed Search','Find the perfect run',(255,200,90)),('card',card,'Card Balance','Small, careful tweaks',(255,150,110))):
    for w,h,out in ((512,512,f'{kind}_square.png'),(1280,420,f'{kind}_banner.png')):
        im=fn(w,h)
        if w>h: title(im,t,s,col,int(h*.66))
        else: title(im,t,s,col,int(h*.66))
        im.save('art_out/'+out)
