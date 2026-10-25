using System;

namespace Guardian.UnitySim
{
    public enum GuardianFaceExpression { Calm, Focused, Happy, Delight, Waiting, Worried, Tired, Sorry, Caution, Stopped }

    /// <summary>Procedural cat-robot face screen (original line art, no third-party texture).
    /// Writes bottom-up RGBA32 rows so Unity can upload it with Texture2D.LoadRawTextureData.
    /// No UnityEngine dependency: faces can be rendered offline for review.</summary>
    public sealed class GuardianFacePainter
    {
        public readonly int Width,Height;
        public readonly byte[] Pixels;
        private float r,g,b; // current stroke colour
        public GuardianFacePainter(int width=192,int height=128) {Width=width;Height=height;Pixels=new byte[width*height*4];}

        /// <param name="gazeX">-1 (image left) .. 1 (image right)</param>
        /// <param name="blink">0 open .. 1 closed</param>
        /// <param name="phase">seconds, for small animations (dots, sparkle)</param>
        public void Paint(GuardianFaceExpression face,float lineR,float lineG,float lineB,float gazeX,float gazeY,float blink,float phase)
        {
            Background();
            r=lineR;g=lineG;b=lineB;
            float h=Height,w=Width,ly=h*.56f,lx=w*.31f,rx=w*.69f,eye=h*.17f,stroke=h*.05f;
            float px=Math.Clamp(gazeX,-1,1)*h*.06f,py=Math.Clamp(gazeY,-1,1)*h*.05f;
            bool closed=blink>.5f;
            switch(face)
            {
                case GuardianFaceExpression.Happy:
                case GuardianFaceExpression.Delight:
                    // ^ ^ eyes and an open smile.
                    Arc(lx,ly-h*.04f,eye*.8f,15,165,stroke);Arc(rx,ly-h*.04f,eye*.8f,15,165,stroke);
                    HalfDisc(w*.5f,h*.30f,h*.085f);
                    Whiskers(stroke);
                    if(face==GuardianFaceExpression.Delight)
                    {
                        Blush(w*.16f,h*.33f);Blush(w*.84f,h*.33f);
                        float twinkle=.75f+.25f*MathF.Sin(phase*7);
                        Sparkle(w*.89f,h*.80f,h*.09f*twinkle);Sparkle(w*.11f,h*.82f,h*.06f*(1.75f-twinkle));
                    }
                    break;
                case GuardianFaceExpression.Focused:
                    if(closed) ClosedEyes(lx,rx,ly,eye,stroke);
                    else {RingEyes(lx,rx,ly,eye,stroke,px,py,.8f);Segment(lx-eye,ly+eye*.62f,lx+eye,ly+eye*.62f,stroke*.9f);Segment(rx-eye,ly+eye*.62f,rx+eye,ly+eye*.62f,stroke*.9f);}
                    Segment(w*.46f,h*.29f,w*.54f,h*.29f,stroke*.8f);
                    Whiskers(stroke);
                    break;
                case GuardianFaceExpression.Waiting:
                    if(closed) ClosedEyes(lx,rx,ly,eye,stroke);
                    else RingEyes(lx,rx,ly,eye,stroke,(MathF.Sin(phase*2.2f)>0?1:-1)*h*.07f,h*.01f,1);
                    for(int i=0;i<3;i++) {float on=((int)(phase*3)%4)>i?1:.25f;Disc(w*(.44f+i*.06f),h*.27f,h*.022f,on);}
                    Whiskers(stroke);
                    break;
                case GuardianFaceExpression.Worried:
                    if(closed) ClosedEyes(lx,rx,ly,eye*.9f,stroke);
                    else RingEyes(lx,rx,ly,eye*.9f,stroke,px*.5f,-h*.04f,1);
                    // Inner brow ends raised.
                    Segment(lx-eye,ly+eye*1.25f,lx+eye*.8f,ly+eye*1.65f,stroke*.8f);Segment(rx-eye*.8f,ly+eye*1.65f,rx+eye,ly+eye*1.25f,stroke*.8f);
                    Wave(w*.40f,w*.60f,h*.28f,h*.022f,stroke*.75f,2);
                    Whiskers(stroke);
                    break;
                case GuardianFaceExpression.Tired:
                    // Half-closed eyes with a lid line, and a sweat drop.
                    Arc(lx,ly,eye*.85f,195,345,stroke);Arc(rx,ly,eye*.85f,195,345,stroke);
                    Segment(lx-eye,ly-eye*.12f,lx+eye,ly-eye*.12f,stroke);Segment(rx-eye,ly-eye*.12f,rx+eye,ly-eye*.12f,stroke);
                    Drop(w*.88f,h*.70f+MathF.Sin(phase*2)*h*.02f,h*.06f);
                    Wave(w*.43f,w*.57f,h*.27f,h*.018f,stroke*.7f,1.5f);
                    break;
                case GuardianFaceExpression.Sorry:
                    // > < eyes.
                    Segment(lx-eye*.7f,ly+eye*.7f,lx+eye*.5f,ly,stroke);Segment(lx+eye*.5f,ly,lx-eye*.7f,ly-eye*.7f,stroke);
                    Segment(rx+eye*.7f,ly+eye*.7f,rx-eye*.5f,ly,stroke);Segment(rx-eye*.5f,ly,rx+eye*.7f,ly-eye*.7f,stroke);
                    Wave(w*.42f,w*.58f,h*.27f,h*.02f,stroke*.75f,2);
                    Drop(w*.12f,h*.72f,h*.05f);
                    break;
                case GuardianFaceExpression.Caution:
                    // Startled: wide rings, tiny pupils, "!" mark.
                    Ring(lx,ly,eye*1.08f,stroke);Ring(rx,ly,eye*1.08f,stroke);
                    Disc(lx+px*.5f,ly+py*.5f,h*.035f,1);Disc(rx+px*.5f,ly+py*.5f,h*.035f,1);
                    Segment(w*.9f,h*.84f,w*.9f,h*.66f,stroke*1.1f);Disc(w*.9f,h*.58f,stroke*.7f,1);
                    Ring(w*.5f,h*.27f,h*.04f,stroke*.8f);
                    break;
                case GuardianFaceExpression.Stopped:
                    // x x eyes and a flat mouth.
                    foreach(float cx in new[]{lx,rx}) {Segment(cx-eye*.75f,ly-eye*.75f,cx+eye*.75f,ly+eye*.75f,stroke*1.1f);Segment(cx-eye*.75f,ly+eye*.75f,cx+eye*.75f,ly-eye*.75f,stroke*1.1f);}
                    Segment(w*.42f,h*.27f,w*.58f,h*.27f,stroke);
                    break;
                default: // Calm
                    if(closed) ClosedEyes(lx,rx,ly,eye,stroke);
                    else RingEyes(lx,rx,ly,eye,stroke,px,py,1);
                    Disc(w*.5f,h*.37f,h*.02f,1);
                    // Cat mouth.
                    Arc(w*.5f-h*.045f,h*.33f,h*.045f,200,340,stroke*.75f);Arc(w*.5f+h*.045f,h*.33f,h*.045f,200,340,stroke*.75f);
                    Whiskers(stroke);
                    break;
            }
        }

        private void Background()
        {
            // Dark rounded screen with a soft vertical gradient; transparent outside the corners.
            float radius=Height*.2f;
            for(int y=0;y<Height;y++) for(int x=0;x<Width;x++)
            {
                float dx=MathF.Max(0,MathF.Abs(x+.5f-Width*.5f)-(Width*.5f-radius)),dy=MathF.Max(0,MathF.Abs(y+.5f-Height*.5f)-(Height*.5f-radius));
                float edge=Math.Clamp(radius-MathF.Sqrt(dx*dx+dy*dy)+.5f,0,1);
                float t=(float)y/Height;int i=(y*Width+x)*4;
                Pixels[i]=(byte)(8+10*t);Pixels[i+1]=(byte)(12+14*t);Pixels[i+2]=(byte)(20+20*t);Pixels[i+3]=(byte)(255*edge);
            }
        }
        private void Blend(int x,int y,float coverage,float cr,float cg,float cb)
        {
            if(coverage<=0 || x<0 || y<0 || x>=Width || y>=Height) return;
            int i=(y*Width+x)*4;if(Pixels[i+3]==0) return;coverage=MathF.Min(1,coverage);
            Pixels[i]=(byte)(Pixels[i]+(cr*255-Pixels[i])*coverage);Pixels[i+1]=(byte)(Pixels[i+1]+(cg*255-Pixels[i+1])*coverage);Pixels[i+2]=(byte)(Pixels[i+2]+(cb*255-Pixels[i+2])*coverage);
        }
        /// <summary>Rasterize a signed-distance shape over its bounding box with 1px anti-aliasing and a faint glow.</summary>
        private void Shape(float x0,float y0,float x1,float y1,Func<float,float,float> distance,float alpha=1,float cr=-1,float cg=0,float cb=0)
        {
            if(cr<0) {cr=r;cg=g;cb=b;}
            const float glow=3;
            int ix0=Math.Max(0,(int)(x0-glow-1)),iy0=Math.Max(0,(int)(y0-glow-1)),ix1=Math.Min(Width-1,(int)(x1+glow+1)),iy1=Math.Min(Height-1,(int)(y1+glow+1));
            for(int y=iy0;y<=iy1;y++) for(int x=ix0;x<=ix1;x++)
            {
                float d=distance(x+.5f,y+.5f);
                float core=Math.Clamp(.5f-d,0,1),halo=d>0 && d<glow?.18f*(1-d/glow):0;
                Blend(x,y,(core+halo*(1-core))*alpha,cr,cg,cb);
            }
        }
        private void Disc(float cx,float cy,float radius,float alpha)=>Shape(cx-radius,cy-radius,cx+radius,cy+radius,(x,y)=>Len(x-cx,y-cy)-radius,alpha);
        private void Ring(float cx,float cy,float radius,float width)=>Shape(cx-radius-width,cy-radius-width,cx+radius+width,cy+radius+width,(x,y)=>MathF.Abs(Len(x-cx,y-cy)-radius)-width*.5f);
        private void Segment(float ax,float ay,float bx,float by,float width)
        {
            Shape(MathF.Min(ax,bx)-width,MathF.Min(ay,by)-width,MathF.Max(ax,bx)+width,MathF.Max(ay,by)+width,(x,y)=>
            {
                float vx=bx-ax,vy=by-ay,t=Math.Clamp(((x-ax)*vx+(y-ay)*vy)/MathF.Max(1e-4f,vx*vx+vy*vy),0,1);
                return Len(x-ax-vx*t,y-ay-vy*t)-width*.5f;
            });
        }
        /// <summary>Ring restricted to [from,to] degrees (0=+x, counter-clockwise, y up) with round caps.</summary>
        private void Arc(float cx,float cy,float radius,float from,float to,float width)
        {
            float a0=from*MathF.PI/180,a1=to*MathF.PI/180,mid=(a0+a1)*.5f,half=(a1-a0)*.5f;
            Shape(cx-radius-width,cy-radius-width,cx+radius+width,cy+radius+width,(x,y)=>
            {
                float dx=x-cx,dy=y-cy,ang=MathF.Atan2(dy,dx),delta=MathF.Abs(Wrap(ang-mid));
                if(delta<=half) return MathF.Abs(Len(dx,dy)-radius)-width*.5f;
                float end=mid+(Wrap(ang-mid)>0?half:-half),ex=cx+radius*MathF.Cos(end),ey=cy+radius*MathF.Sin(end);
                return Len(x-ex,y-ey)-width*.5f;
            });
        }
        private void HalfDisc(float cx,float cy,float radius)
            =>Shape(cx-radius,cy-radius,cx+radius,cy,(x,y)=>MathF.Max(Len(x-cx,y-cy)-radius,y-cy));
        private void Wave(float x0,float x1,float cy,float amplitude,float width,float cycles)
        {
            int steps=10;float px=x0,py=cy;
            for(int i=1;i<=steps;i++) {float t=(float)i/steps,x=x0+(x1-x0)*t,y=cy+amplitude*MathF.Sin(t*cycles*2*MathF.PI);Segment(px,py,x,y,width);px=x;py=y;}
        }
        private void RingEyes(float lx,float rx,float ly,float eye,float stroke,float px,float py,float pupil)
        {
            Ring(lx,ly,eye,stroke);Ring(rx,ly,eye,stroke);
            float p=Height*.07f*pupil;Disc(lx+px,ly+py,p,1);Disc(rx+px,ly+py,p,1);
        }
        private void ClosedEyes(float lx,float rx,float ly,float eye,float stroke)
        {Arc(lx,ly+eye*.25f,eye*.75f,200,340,stroke);Arc(rx,ly+eye*.25f,eye*.75f,200,340,stroke);}
        private void Whiskers(float stroke)
        {
            float w=Width,h=Height;
            Segment(w*.04f,h*.40f,w*.15f,h*.37f,stroke*.55f);Segment(w*.04f,h*.29f,w*.15f,h*.31f,stroke*.55f);
            Segment(w*.96f,h*.40f,w*.85f,h*.37f,stroke*.55f);Segment(w*.96f,h*.29f,w*.85f,h*.31f,stroke*.55f);
        }
        private void Blush(float cx,float cy)
        {
            float rx=Height*.1f,ry=Height*.045f;
            Shape(cx-rx,cy-ry,cx+rx,cy+ry,(x,y)=>(Len((x-cx)/rx,(y-cy)/ry)-1)*ry,.55f,1f,.45f,.62f);
        }
        private void Sparkle(float cx,float cy,float size)
        {
            Segment(cx-size,cy,cx+size,cy,size*.22f);Segment(cx,cy-size,cx,cy+size,size*.22f);
            Segment(cx-size*.5f,cy-size*.5f,cx+size*.5f,cy+size*.5f,size*.12f);Segment(cx-size*.5f,cy+size*.5f,cx+size*.5f,cy-size*.5f,size*.12f);
        }
        private void Drop(float cx,float cy,float size)
        {
            // Teardrop: disc plus a tip pointing up.
            Shape(cx-size,cy-size,cx+size,cy+size*1.9f,(x,y)=>
            {
                float d=Len(x-cx,y-cy)-size;
                if(y>cy) d=MathF.Min(d,MathF.Abs(x-cx)-size*(1-(y-cy)/(size*1.8f)));
                return MathF.Max(d,y-cy-size*1.8f);
            },.9f,.55f,.85f,1f);
        }
        private static float Len(float x,float y)=>MathF.Sqrt(x*x+y*y);
        private static float Wrap(float a) {while(a>MathF.PI) a-=2*MathF.PI;while(a<-MathF.PI) a+=2*MathF.PI;return a;}
    }
}
