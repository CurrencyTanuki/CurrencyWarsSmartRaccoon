using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

// 裁剪 132307 帧：①千冶刃(后排 slot5)的装备带 ②物品栏——存 PNG 供用户查看。
public sealed class CropRegionProbe
{
    [Fact]
    public void Run()
    {
        var root = @"D:\CWAFix-20260814";
        var framePath = System.IO.Path.Combine(root, "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "phase2-2026-07-28", "132307.png");
        var bgra = Cv2.ImRead(framePath, ImreadModes.Unchanged); // BGRA
        int W=bgra.Width,H=bgra.Height;
        System.Console.WriteLine($"[CROP] frame {W}x{H}");

        // ① 千冶刃 后排 slot5 = BackCharacterSlots1920(9)[1]
        var backAll = Phase2RecognitionRegions.BackCharacterSlots1920(9);
        for (int bi=0; bi<Math.Min(3,backAll.Count); bi++)
        {
            var bnds = backAll[bi];
            var bnd = Phase2RecognitionRegions.CharacterEquipmentIconBand(bnds);
            var bpx = bnd.ToPixels(W,H);
            SaveCrop(bgra, bpx, System.IO.Path.Combine(root,$"tools\\crop_backband_{bi}.png"), 3, $"BackBand idx{bi}");
            System.Console.WriteLine($"[CROP] back idx{bi} band="+bnd+" px="+bpx);
        }

        // ② 物品栏整体区域
        var invSlots = Phase2RecognitionRegions.InventoryIconSlots;
        if (invSlots is { Count: > 0 })
        {
            double minX=1,minY=1,maxX=0,maxY=0;
            foreach (var s in invSlots){ var p=s.ToPixels(W,H); minX=Math.Min(minX,(double)p.X/W); minY=Math.Min(minY,(double)p.Y/H); maxX=Math.Max(maxX,(double)p.Right/W); maxY=Math.Max(maxY,(double)p.Bottom/H); }
            var invRect = new PixelRect((int)(minX*W), (int)(minY*H), (int)((maxX-minX)*W), (int)((maxY-minY)*H));
            SaveCrop(bgra, invRect, System.IO.Path.Combine(root,"tools","crop_inventory.png"),1,"inventory");
            System.Console.WriteLine("[CROP] inventory rect="+invRect+" slots="+invSlots.Count);
        }
    }

    static void SaveCrop(Mat src, PixelRect r, string path, double scale, string label)
    {
        int x=Math.Max(0,(int)r.X), y=Math.Max(0,(int)r.Y);
        int w=Math.Min(src.Width-x,(int)r.Width), h=Math.Min(src.Height-y,(int)r.Height);
        if(w<=0||h<=0){ System.Console.WriteLine("[CROP] empty "+label); return; }
        using var roi = new Mat(src, new Rect(x,y,w,h));
        using var small = new Mat();
        Cv2.Resize(roi, small, new OpenCvSharp.Size((int)(w*scale),(int)(h*scale)));
        Cv2.ImWrite(path, small);
        System.Console.WriteLine("[CROP] saved "+label+" -> "+path+" ("+w+"x"+h+" -> "+small.Width+"x"+small.Height+")");
    }
}
