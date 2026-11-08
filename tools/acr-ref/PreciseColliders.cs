using static AcrRef.Splines;
namespace AcrRef;

internal static class PreciseColliders
{
    // Clip actual triangles to each voxel column, including vertical and thin surfaces.
    internal static List<ObjectColliders.Shape>? Split(ObjectColliders.Shape shape, V3[] triangles, double cell, double band, int cap)
    {
        if (triangles.Length < 3 || shape.Box is not { } b || cell <= 0 || band <= 0 || cap < 1) return null;
        var (x, y, z) = ObjectColliders.RotationAxes(b.Rot[0], b.Rot[1], b.Rot[2]);
        double Dot(V3 a, V3 c) => a.X*c.X+a.Y*c.Y+a.Z*c.Z;
        var points = triangles.Select(p => { var d = p-b.Center; return new V3(Dot(d,x),Dot(d,y),Dot(d,z)); }).ToArray();
        double bottom = -b.Z/2, top = Math.Min(b.Z/2, points.Min(p=>p.Z)+band);
        int nx=(int)Math.Ceiling(b.X/cell), ny=(int)Math.Ceiling(b.Y/cell);
        if (nx <= 0 || ny <= 0 || (long)nx*ny > 1000000 || top <= bottom) return null;
        var heights = new double[nx,ny];
        for(int i=0;i<nx;i++) for(int j=0;j<ny;j++) heights[i,j]=double.NegativeInfinity;
        double Coord(V3 p,int axis) => axis==0?p.X:axis==1?p.Y:p.Z;
        List<V3> Clip(List<V3> poly,int axis,double edge,bool above)
        {
            var result=new List<V3>();
            if(poly.Count==0) return result;
            var a=poly[^1]; double da=(Coord(a,axis)-edge)*(above?1:-1);
            foreach(var c in poly) {
                double dc=(Coord(c,axis)-edge)*(above?1:-1);
                if((da>=0)!=(dc>=0)) result.Add(a+(c-a)*(da/(da-dc)));
                if(dc>=0) result.Add(c);
                a=c; da=dc;
            }
            return result;
        }
        for(int t=0;t+2<points.Length;t+=3) {
            var poly=Clip(Clip(new List<V3>{points[t],points[t+1],points[t+2]},2,bottom,true),2,top,false);
            if(poly.Count==0) continue;
            int ix(double v)=>Math.Clamp((int)Math.Floor((v+b.X/2)/cell),0,nx-1);
            int iy(double v)=>Math.Clamp((int)Math.Floor((v+b.Y/2)/cell),0,ny-1);
            for(int i=ix(poly.Min(p=>p.X));i<=ix(poly.Max(p=>p.X));i++)
            for(int j=iy(poly.Min(p=>p.Y));j<=iy(poly.Max(p=>p.Y));j++) {
                var q=Clip(Clip(Clip(Clip(poly,0,-b.X/2+i*cell,true),0,Math.Min(b.X/2,-b.X/2+(i+1)*cell),false),1,-b.Y/2+j*cell,true),1,Math.Min(b.Y/2,-b.Y/2+(j+1)*cell),false);
                if(q.Count>0) heights[i,j]=Math.Max(heights[i,j],q.Max(p=>p.Z));
            }
        }
        var rectangles=new List<(int a,int end,int row,int last,double high)>();
        for(int j=0;j<ny;j++) for(int i=0;i<nx;) {
            if(!double.IsFinite(heights[i,j]) || heights[i,j]<=bottom) {i++;continue;}
            // a run (and a merge across rows) only while the drawn top stays within 15 cm: a box over a bonnet must not take the roof
            // height, or it stands above what is drawn and gets a stand-in again
            const double tol = 15;
            int a=i; double h=heights[i,j], lo=h;
            while(i<nx && double.IsFinite(heights[i,j]) && heights[i,j]>bottom
                  && Math.Max(h,heights[i,j]) - Math.Min(lo,heights[i,j]) <= tol) { h=Math.Max(h,heights[i,j]); lo=Math.Min(lo,heights[i,j]); i++; }
            int k=rectangles.FindIndex(r=>r.a==a && r.end==i && r.last==j-1 && Math.Abs(r.high-h) <= tol);
            if(k>=0) {var r=rectangles[k]; rectangles[k]=(a,i,r.row,j,Math.Max(h,r.high));}
            else rectangles.Add((a,i,j,j,h));
        }
        if(rectangles.Count==0 || rectangles.Count>cap) return null;
        return rectangles.Select(r=> {
            double left=-b.X/2+r.a*cell,right=Math.Min(b.X/2,-b.X/2+r.end*cell), front=-b.Y/2+r.row*cell,back=Math.Min(b.Y/2,-b.Y/2+(r.last+1)*cell);
            var box=new ObjectColliders.Box {Center=b.Center+x*((left+right)/2)+y*((front+back)/2)+z*((bottom+r.high)/2),Rot=(double[])b.Rot.Clone(),X=right-left,Y=back-front,Z=r.high-bottom};
            var s=new ObjectColliders.Shape {Box=box,Kind=shape.Kind,Material=shape.Material,Template=shape.Template,Instance=shape.Instance};
            foreach(double a in new[]{-1.0,1.0}) foreach(double c in new[]{-1.0,1.0}) foreach(double d in new[]{-1.0,1.0}) s.FootprintUe.Add(box.Center+x*(a*box.X/2)+y*(c*box.Y/2)+z*(d*box.Z/2));
            return s;
        }).ToList();
    }
}
