using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using GbaMultiMenuBuilder.Services;

namespace GbaMultiMenuBuilder.Fc;

public sealed record FcTitleBitmap(byte[] List, byte[] Detail, string MissingCharacters);

// Final row-major 1bpp strips, rendered with the same 12px font as GBC.
public static class FcTitleRenderer
{
    public const int GlyphPixels=12, RowBytes=32, ListWidth=208, DetailWidth=224;
    public const int ListBytes=512, DetailBytes=1024;
    public const string FontFileName="mplus_hzk_12.ttf";
    private static readonly object Gate=new();
    private static readonly Dictionary<int,byte[]> Glyphs=[];
    private static readonly HashSet<int> Missing=[];
    private static readonly Dictionary<string,FcTitleBitmap> Titles=[];
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)]
    private static extern uint GetGlyphIndicesW(IntPtr dc,string text,int count,[Out] ushort[] indices,uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);

    public static FcTitleBitmap Render(string name)
    {
        name=string.IsNullOrWhiteSpace(name)?"UNKNOWN":name.Trim();
        // The Unicode card entry holds 46 UTF-16 units; preview and raster must
        // use that same stored name so a later scan/rewrite stays identical.
        if(name.Length>46)name=name[..46];
        if(name.Length>0&&char.IsHighSurrogate(name[^1]))name=name[..^1];
        lock(Gate)
        {
            if(Titles.TryGetValue(name,out var existing))return existing;
            var codes=name.EnumerateRunes().Select(r=>Rune.IsControl(r)?32:r.Value).ToArray();
            var needed=codes.Append((int)'.').Append(0x25a1).Distinct().Where(c=>!Glyphs.ContainsKey(c)).ToArray();
            if(needed.Length>0)
            {
                string path=Path.Combine(AppContext.BaseDirectory,"Assets","Fonts",FontFileName);
                if(!File.Exists(path))path=Path.Combine(Directory.GetCurrentDirectory(),"Assets","Fonts",FontFileName);
                if(!File.Exists(path))throw new FileNotFoundException("FC requires the GBC 12px font.",path);
                var rendered=GbcFontService.BuildGlyphCacheDetailed(needed,path);
                if(rendered.Format.GlyphSizePx!=12||rendered.Format.GlyphBytes!=24)throw new InvalidDataException("FC titles require 12px glyphs.");
                GbcMenuLayerBuilder.ApplyMenuPunctuationOverrides(rendered.Glyphs,rendered.Format);
                // M+ HZK has no U+00B7. Supply the literal centered punctuation
                // used in Chinese game names, without relying on a system font.
                if(needed.Contains(0x00b7)){byte[] dot=new byte[24];dot[10]=6;dot[12]=6;rendered.Glyphs[0x00b7]=dot;}
                using var fonts=new PrivateFontCollection();fonts.AddFontFile(path);
                using var font=new Font(fonts.Families[0],12,FontStyle.Regular,GraphicsUnit.Pixel);
                using var bitmap=new Bitmap(12,12);using var graphics=Graphics.FromImage(bitmap);
                IntPtr dc=graphics.GetHdc(),handle=font.ToHfont(),previous=SelectObject(dc,handle);
                try
                {
                    foreach(int c in needed)
                    {
                        ushort[] index=[0xffff];bool supported=c==0x00b7||(c<=ushort.MaxValue&&
                            GetGlyphIndicesW(dc,char.ConvertFromUtf32(c),1,index,1)!=uint.MaxValue&&index[0]!=0xffff);
                        if(!supported||c!=32&&rendered.Glyphs[c].All(b=>b==0))Missing.Add(c);
                        Glyphs[c]=rendered.Glyphs[c];
                    }
                }
                finally{SelectObject(dc,previous);DeleteObject(handle);graphics.ReleaseHdc(dc);}
            }
            byte[] Glyph(int c)=>Glyphs[Missing.Contains(c)?0x25a1:c];
            int Next(int c,int x,bool list)
            {
                // Match GBC's list cells: ASCII 6px, Chinese 12px aligned cells.
                if(list)return c<128?x+6:x+12+(x%12==0?0:6);
                if(c==32)return x+4;
                if(c>=128)return x+13;
                var glyph=Glyph(c);int right=-1;
                for(int y=0;y<12;y++)for(int bit=0;bit<12;bit++)
                    if((glyph[y*2+(bit>>3)]&(0x80>>(bit&7)))!=0)right=Math.Max(right,bit);
                return x+Math.Max(1,right+1)+1;
            }
            void Blit(byte[] target,int c,int x,int top,int width)
            {
                var glyph=Glyph(c);
                for(int y=0;y<12;y++)for(int bit=0;bit<12&&x+bit<width;bit++)
                    if((glyph[y*2+(bit>>3)]&(0x80>>(bit&7)))!=0)
                        target[(top+y)*RowBytes+((x+bit)>>3)]|=(byte)(0x80>>((x+bit)&7));
            }
            int Line(byte[] target,int start,int top,int width,bool list,bool ellipsis)
            {
                List<(int Code,int X)> placed=[];int x=0,i=start;
                for(;i<codes.Length;i++){
                    int next=Next(codes[i],x,list);if(next>width)break;
                    placed.Add((codes[i],x));x=next;
                }
                if(ellipsis&&i<codes.Length){
                    int DotEnd(int at)=>Next(46,Next(46,Next(46,at,list),list),list);
                    while(placed.Count>0&&DotEnd(x)>width){x=placed[^1].X;placed.RemoveAt(placed.Count-1);}
                    for(int j=0;j<3;j++){placed.Add((46,x));x=Next(46,x,list);}
                }
                foreach(var glyph in placed)Blit(target,glyph.Code,glyph.X,top,width);
                return i;
            }
            byte[] listPixels=new byte[ListBytes],detailPixels=new byte[DetailBytes];
            Line(listPixels,0,0,ListWidth,true,true);
            int second=Line(detailPixels,0,0,DetailWidth,false,false);
            Line(detailPixels,second,16,DetailWidth,false,true);
            var result=new FcTitleBitmap(listPixels,detailPixels,string.Concat(codes.Where(Missing.Contains).Distinct().Select(char.ConvertFromUtf32)));
            if(Titles.Count>=512)Titles.Clear();
            Titles[name]=result;return result;
        }
    }

    public static Bitmap Preview(byte[] pixels,int width,int height,int scale=4)
    {
        var image=new Bitmap(width*scale,height*scale);
        using var g=Graphics.FromImage(image);g.Clear(Color.White);
        using var ink=new SolidBrush(Color.Black);
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            if((pixels[y*RowBytes+(x>>3)]&(0x80>>(x&7)))!=0)g.FillRectangle(ink,x*scale,y*scale,scale,scale);
        return image;
    }
}
