using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 诊断：对 132307 帧，裁出 Aglaia(slot0)/Sunday(slot3)/千冶刃(Back5) 的装备带
// 逐槽算 isBlue 占比与前景强度，对比为什么 Sunday 永动机识别、Aglaia 轮滑鞋漏、后排全漏。
public sealed class EquipBandDiagProbe
{
    [Fact]
    public void Run()
    {
        var root = @"D:\CWAFix-20260814";
        var frame = CaptureFrameLoader.LoadFile(System.IO.Path.Combine(root, "tests", "CurrencyWarsAssistant.Tests", "Fixtures", "phase2-2026-07-28", "132307.png"));
        int W=frame.Width, H=frame.Height;
        // 角色 ReferenceBounds（Front slot0 阿格莱雅 / slot3 星期日 / Back slot5 千冶刃）
        var rows = new (string label, int zoneSlot)[]
        {
            ("Aglaia F0", 0), ("Sunday F3", 3), ("QianYe B5", 5)
        };
        foreach (var (label, zi) in rows)
        {
            var zone = zi >= 4 ? FormationZone.Back : FormationZone.Front;
            var idx = zi >= 4 ? zi - 4 : zi;
            var slots = zone == FormationZone.Back
                ? Phase2RecognitionRegions.BackCharacterSlots1920(9)
                : Phase2RecognitionRegions.PreparationCharacterSlots1920;
            // slots 是 3 元组? 用已知 Front 4 + Back 的方式; 这里用 boardReference 同源
            var bounds = zone == FormationZone.Back
                ? Phase2RecognitionRegions.BackCharacterSlots1920(9)[idx]
                : Phase2RecognitionRegions.PreparationCharacterSlots1920[idx];
            System.Console.WriteLine($"== {label} bounds={bounds}");
            // 装备带
            var band = Phase2RecognitionRegions.CharacterEquipmentIconBand(bounds);
            var bp = band.ToPixels(W, H);
            // 逐固定槽 (Front:0.03/0.39/0.72, Back:0.03/0.34/0.68)
            double[] starts = zone == FormationZone.Back ? new[]{0.03,0.34,0.68} : new[]{0.03,0.39,0.72};
            for (int k=0;k<starts.Length;k++){
                double f=starts[k];
                var xStart=bounds.X+bounds.Width*f;
                var yCenter=bounds.Y+bounds.Height*1.08;
                double slotW=bounds.Width*0.26, slotH=bounds.Height*0.26;
                var sl = new NormalizedRect(xStart/1920.0,(yCenter-slotH/2)/1080.0,slotW/1920.0,slotH/1080.0);
                var sp=sl.ToPixels(W,H);
                // 统计该槽 isBlue 与"非蓝"(段检测判据)
                double nonBlue=0, isBlue=0, total=0; double brMin=9999, brMax=-9999;
                for (int yy=sp.Y; yy<sp.Bottom && yy<H; yy++){
                    var off=yy*frame.Stride+sp.X*4;
                    for(int xx=sp.X; xx<sp.Right && xx<W; xx++){
                        var o=off+(xx-sp.X)*4;
                        int b=frame.BgraPixels[o],g=frame.BgraPixels[o+1],r=frame.BgraPixels[o+2];
                        bool bl = b>r+25 && b>g+10 && b>70;
                        double br=b-r;
                        if(br<brMin)brMin=br; if(br>brMax)brMax=br;
                        if(bl)isBlue++; else nonBlue++;
                        total++;
                    }
                }
                System.Console.WriteLine($"   slot{k}(f={f}): w={sp.Width} h={sp.Height} 非蓝占={nonBlue/total*100:F0}% isBlue占={isBlue/total*100:F0}% B-R范围[{brMin:F0},{brMax:F0}]");
            }
        }
        System.Console.WriteLine("另: 全图非蓝段诊断等下一步");
    }
}
