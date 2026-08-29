using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.Tests;

// 批量测：phase2-2026-07-28 目录 6 张备战帧的后排角色识别结果（能认出谁 / unknown）。
public sealed class BatchBackProbe
{
    [Fact]
    public async Task Run()
    {
        var root = @"D:\CWAFix-20260814";
        using var characterRecognizer = new OpenCvCharacterCardRecognizer(
                lenientLeadOverCharacterIds: ["currency_wars_character_40","currency_wars_character_56","currency_wars_character_72","currency_wars_character_trailblazer"]);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var dd=System.IO.Path.Combine(root,"data","4.4");
        var gd=GameDataCatalogLoader.Load(dd);
        var analyzer=new Phase2OperationalScreenshotAnalyzer(
            characterRecognizer, LoadChar(dd,gd), iconRecognizer, Phase2IconTemplateCatalog.Load(dd),
            new WindowsOfflineOcr("zh-Hans",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4), gd,
            new WindowsOfflineOcr("en-US",OfflineOcrRecognitionMode.Fast,maximumConcurrency:4), enableRobustFallback:false);
        string[] frames={"125924.png","130104.png","130112.png","130123.png","132307.png","132328.png"};
        foreach(var fn in frames){
            var f=CaptureFrameLoader.LoadFile(System.IO.Path.Combine(root,"tests","CurrencyWarsAssistant.Tests","Fixtures","phase2-2026-07-28",fn));
            var st=await analyzer.AnalyzeAsync(f,"preparation_1_1","b:"+fn,new RunSnapshot{RunId="batch",AsOf=f.CapturedAt},default);
            // 汇总后排（Back zone）角色
            var back=st.Formation.Value!.Where(x=>x.Zone==FormationZone.Back).Select(x=> $"{x.CharacterId}").ToArray();
            System.Console.WriteLine($"[BACK] {fn}: "+string.Join(", ", back));
        }
    }
    static IReadOnlyList<CharacterCardTemplateDefinition> LoadChar(string dir,GameDataCatalog g){
        var d=System.IO.Path.Combine(dir,"character-card-templates");
        return g.CurrencyWarsCharacters.SelectMany(c=>System.IO.Directory.GetFiles(d,$"{c.Id}__*.png").Select(x=>new CharacterCardTemplateDefinition(c.Id,c.Name,x))).ToList();
    }
}
