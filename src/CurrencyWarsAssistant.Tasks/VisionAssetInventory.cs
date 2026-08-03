using System.Text.Json;
using CurrencyWarsAssistant.Game;

namespace CurrencyWarsAssistant.Tasks;

public enum VisionAssetCategory
{
    CharacterCard,
    ExpertAdvisor,
    InvestmentEnvironment,
    InvestmentStrategy,
    Equipment,
    SpecialItem
}

public enum VisionAssetAvailability
{
    LocalTemplateAvailable,
    SourceLocatedTemplateMissing,
    UserScreenshotRequired
}

public sealed record VisionAssetInventoryItem(
    string AssetId,
    string DisplayName,
    VisionAssetCategory Category,
    VisionAssetAvailability Availability,
    string? LocalAssetPath,
    Uri SourcePage,
    Uri? SourceImage,
    string LicenseNote,
    string Notes);

public sealed record VisionAssetInventoryReport(
    string GameVersion,
    DateTimeOffset AuditedAt,
    IReadOnlyList<VisionAssetInventoryItem> Items)
{
    public IReadOnlyList<VisionAssetInventoryItem> Missing => Items
        .Where(item => item.Availability !=
                       VisionAssetAvailability.LocalTemplateAvailable)
        .ToArray();
}

public sealed class VisionAssetInventoryBuilder
{
    private static readonly Uri WikiRoot =
        new("https://wiki.biligame.com/sr/");

    public VisionAssetInventoryReport Build(
        GameDataCatalog gameData,
        string dataRoot,
        DateTimeOffset auditedAt)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var fullDataRoot = Path.GetFullPath(dataRoot);
        var items = new List<VisionAssetInventoryItem>();

        AddCharacters(items, gameData, fullDataRoot);
        AddInvestments(items, gameData);
        AddEquipment(items, fullDataRoot);
        AddSpecialItems(items);

        return new VisionAssetInventoryReport(
            "4.4",
            auditedAt,
            items.OrderBy(item => item.Category)
                .ThenBy(item => item.AssetId, StringComparer.Ordinal)
                .ToArray());
    }

    private static void AddCharacters(
        ICollection<VisionAssetInventoryItem> items,
        GameDataCatalog gameData,
        string dataRoot)
    {
        var templateDirectory = Path.Combine(
            dataRoot,
            "4.4",
            "character-card-templates");
        foreach (var character in gameData.CurrencyWarsCharacters)
        {
            var template = Directory.Exists(templateDirectory)
                ? Directory.GetFiles(
                    templateDirectory,
                    $"{character.Id}__*.png").SingleOrDefault()
                : null;
            items.Add(new VisionAssetInventoryItem(
                character.Id,
                character.Name,
                VisionAssetCategory.CharacterCard,
                template is null
                    ? VisionAssetAvailability.UserScreenshotRequired
                    : VisionAssetAvailability.LocalTemplateAvailable,
                template,
                Wiki($"货币战争/{character.Name}"),
                null,
                "本地模板来自用户授权的游戏截图；WIKI 页面仅作名称和版本核对。",
                "第一阶段角色卡模板可直接复用。"));

            if (character.ExpertAdvisorAvailable)
            {
                items.Add(new VisionAssetInventoryItem(
                    $"expert_advisor:{character.Id}",
                    character.Name,
                    VisionAssetCategory.ExpertAdvisor,
                    VisionAssetAvailability.SourceLocatedTemplateMissing,
                    null,
                    Wiki($"货币战争/{character.Name}"),
                    null,
                    "专家顾问身份图标需要从用户截图制作模板；不直接假定普通角色卡模板足够。",
                    "普通角色卡可识别人物，但专家标记和解锁状态仍缺少专用样本。"));
            }
        }
    }

    private static void AddInvestments(
        ICollection<VisionAssetInventoryItem> items,
        GameDataCatalog gameData)
    {
        foreach (var environment in gameData.InvestmentEnvironments)
        {
            items.Add(new VisionAssetInventoryItem(
                environment.Id,
                environment.Name,
                VisionAssetCategory.InvestmentEnvironment,
                VisionAssetAvailability.SourceLocatedTemplateMissing,
                null,
                Wiki(environment.Name),
                null,
                "BWIKI 页面按 CC BY-NC-SA 4.0 提供；游戏图标的再分发许可仍需单独核对。",
                "已定位公开图鉴页，尚未生成经过游戏截图验证的模板。"));
        }

        foreach (var strategy in gameData.InvestmentStrategies)
        {
            items.Add(new VisionAssetInventoryItem(
                strategy.Id,
                strategy.Name,
                VisionAssetCategory.InvestmentStrategy,
                VisionAssetAvailability.SourceLocatedTemplateMissing,
                null,
                Wiki(strategy.Name),
                null,
                "BWIKI 页面按 CC BY-NC-SA 4.0 提供；游戏图标的再分发许可仍需单独核对。",
                "文本/OCR 字典已存在，图标模板仍需公开素材与用户截图交叉验证。"));
        }
    }

    private static void AddEquipment(
        ICollection<VisionAssetInventoryItem> items,
        string dataRoot)
    {
        var equipmentFile = Path.Combine(
            dataRoot,
            "runtime",
            "1.0.0",
            "4.4",
            "equipment",
            "equipment.json");
        if (!File.Exists(equipmentFile))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(equipmentFile));
        foreach (var record in document.RootElement
                     .GetProperty("records")
                     .EnumerateArray())
        {
            var id = record.GetProperty("id").GetString()!;
            var name = record.GetProperty("name").GetString()!;
            var icon = record.GetProperty("icon");
            var relativePath = icon.GetProperty("asset_path").GetString()!;
            var localPath = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(equipmentFile)!,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var sourceImageText = icon.GetProperty("source_url").GetString();
            items.Add(new VisionAssetInventoryItem(
                id,
                name,
                VisionAssetCategory.Equipment,
                File.Exists(localPath)
                    ? VisionAssetAvailability.LocalTemplateAvailable
                    : VisionAssetAvailability.SourceLocatedTemplateMissing,
                File.Exists(localPath) ? localPath : null,
                Wiki(name),
                Uri.TryCreate(sourceImageText, UriKind.Absolute, out var sourceImage)
                    ? sourceImage
                    : null,
                "图标来源记录在现有装备数据中；发布前仍需遵守原站与游戏素材许可。",
                "现有 128/256 像素图标可用于离线匹配候选，必须用实际游戏截图校准阈值。"));
        }
    }

    private static void AddSpecialItems(
        ICollection<VisionAssetInventoryItem> items)
    {
        items.Add(new VisionAssetInventoryItem(
            "special_item:expert_invitation",
            "专家邀请函",
            VisionAssetCategory.SpecialItem,
            VisionAssetAvailability.UserScreenshotRequired,
            null,
            Wiki("4.0版本货币战争调整公告"),
            null,
            "公开资料已确认名称和机制，但未找到可安全直接纳入发布包的独立图标。",
            "需要用户提供物品栏或奖励界面的清晰截图，以制作并验证模板。"));
    }

    private static Uri Wiki(string pageName) => new(
        WikiRoot,
        Uri.EscapeDataString(pageName).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase));
}

