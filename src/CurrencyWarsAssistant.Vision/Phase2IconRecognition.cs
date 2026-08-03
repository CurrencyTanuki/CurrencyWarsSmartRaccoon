using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CurrencyWarsAssistant.Core;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Vision;

public enum Phase2IconComparisonMode
{
    FullFrameGrayscale,
    FullFrameColor,
    ForegroundShape,
    AlphaMaskedColor,
    InventoryAlphaMaskedColor
}

public sealed record Phase2IconTemplateDefinition(
    string Category,
    string Id,
    string FilePath,
    double MinimumConfidence = 0.62,
    bool ResolvesExactIdentity = true,
    IReadOnlyList<string>? CandidateIds = null,
    string? SourceConfidence = null,
    Phase2IconComparisonMode ComparisonMode =
        Phase2IconComparisonMode.FullFrameGrayscale,
    double? MinimumMargin = null,
    string? SemanticKind = null);

public sealed record Phase2IconRecognition(
    int SlotIndex,
    PixelRect Region,
    string? TemplateId,
    double Confidence,
    bool IsKnown,
    IReadOnlyList<string>? CandidateTemplateIds = null,
    IReadOnlyList<Phase2IconCandidate>? RankedCandidates = null);

public sealed record Phase2IconCandidate(
    string TemplateId,
    double Confidence,
    bool ResolvesExactIdentity,
    IReadOnlyList<string> CandidateTemplateIds);

public interface IPhase2IconRecognizer
{
    IReadOnlyList<Phase2IconRecognition> Recognize(
        CaptureFrame frame,
        string category,
        IReadOnlyList<NormalizedRect> slots,
        IReadOnlyList<Phase2IconTemplateDefinition> templates);
}

public static class Phase2IconTemplateCatalog
{
    private static readonly ConcurrentDictionary<string, ImportedCatalogCache>
        ImportedCatalogs = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<Phase2IconTemplateDefinition> Load(
        string dataDirectory)
    {
        var results = new List<Phase2IconTemplateDefinition>();
        var avatarRoot = Path.Combine(
            dataDirectory,
            "character-small-avatar-templates");
        if (Directory.Exists(avatarRoot))
        {
            results.AddRange(Directory.GetFiles(avatarRoot, "*.png")
                .Select(file => new Phase2IconTemplateDefinition(
                    "character-avatar",
                    Path.GetFileNameWithoutExtension(file)
                        .Split("__", 2, StringSplitOptions.None)[0],
                    file,
                    0.48)));
        }

        var actionIndicatorRoot = Path.Combine(
            dataDirectory,
            "phase2-action-indicator-templates");
        if (Directory.Exists(actionIndicatorRoot))
        {
            results.AddRange(Directory.GetFiles(actionIndicatorRoot, "*.png")
                .Select(file => new Phase2IconTemplateDefinition(
                    "action-value-indicator",
                    Path.GetFileNameWithoutExtension(file),
                    file,
                    0.45)));
        }

        var actionDigitRoot = Path.Combine(
            dataDirectory,
            "action-digit-templates");
        if (Directory.Exists(actionDigitRoot))
        {
            results.AddRange(Directory.GetFiles(actionDigitRoot, "*.png")
                .Select(file => new Phase2IconTemplateDefinition(
                    "action-value-digit",
                    Path.GetFileNameWithoutExtension(file),
                    file,
                    0.58)));
        }

        // The verified equipment data pipeline already publishes all icon
        // assets under data/runtime. Equipment category is determined by the
        // UI slot in which an icon is observed, so the same source catalog is
        // intentionally exposed to both inventory and character-card readers.
        var dataRoot = Directory.GetParent(dataDirectory)?.FullName;
        var equipmentRoot = dataRoot is null
            ? string.Empty
            : Path.Combine(
                dataRoot,
                "runtime",
                "1.0.0",
                Path.GetFileName(dataDirectory),
                "equipment",
                "assets",
                "currency_wars_equipment_icons");
        if (Directory.Exists(equipmentRoot))
        {
            var equipmentKinds = LoadEquipmentKinds(equipmentRoot);
            var equipmentTemplates = Directory.GetFiles(equipmentRoot, "*.png")
                .SelectMany<string, EquipmentTemplateSource>(file =>
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (!equipmentKinds.TryGetValue(id, out var kind))
                    {
                        return Array.Empty<EquipmentTemplateSource>();
                    }

                    var slotCategory = kind.Category == "basic"
                        ? "simple-equipment"
                        : kind.OccupiesEquipmentSlot
                            ? "advanced-equipment"
                            : null;
                    var sha256 = Convert.ToHexString(SHA256.HashData(
                        File.ReadAllBytes(file)));
                    var semanticKind = kind.Category == "basic"
                        ? "simple-equipment"
                        : kind.OccupiesEquipmentSlot
                            ? "advanced-equipment"
                            : kind.Category.Contains(
                                "dismantle",
                                StringComparison.OrdinalIgnoreCase)
                                ? "dismantle-tool"
                                : "special-item";
                    var inventoryComparisonMode =
                        Phase2IconComparisonMode.InventoryAlphaMaskedColor;
                    var sources = new List<EquipmentTemplateSource>
                    {
                        new(
                            "inventory-item",
                            id,
                            file,
                            sha256,
                            semanticKind,
                            inventoryComparisonMode)
                    };
                    if (slotCategory is not null)
                    {
                        sources.Add(new EquipmentTemplateSource(
                            slotCategory,
                            id,
                            file,
                            sha256,
                            semanticKind,
                            Phase2IconComparisonMode.AlphaMaskedColor));
                    }

                    return sources;
                })
                .GroupBy(
                    item => (
                        item.Category,
                        item.SemanticKind,
                        item.ComparisonMode,
                        item.Sha256),
                    item => item)
                .Select(group =>
                {
                    var identities = group
                        .OrderBy(item => item.Id, StringComparer.Ordinal)
                        .ToArray();
                    var canonical = identities[0];
                    var candidateIds = identities
                        .Select(item => item.Id)
                        .ToArray();
                    return new Phase2IconTemplateDefinition(
                        canonical.Category,
                        canonical.Id,
                        canonical.FilePath,
                        // Equipment icons are rendered at roughly 45-55 px
                        // with UI borders and stack counters. This remains a
                        // category-local threshold; no global threshold changes.
                        0.30,
                        ResolvesExactIdentity: candidateIds.Length == 1,
                        CandidateIds: candidateIds,
                        SourceConfidence: candidateIds.Length == 1
                            ? "verified unique visual identity"
                            : "multiple standard IDs share identical icon bytes",
                        ComparisonMode: canonical.ComparisonMode,
                        SemanticKind: canonical.SemanticKind);
                });
            results.AddRange(equipmentTemplates);
        }

        var root = Path.Combine(dataDirectory, "phase2-icon-templates");
        if (Directory.Exists(root))
        {
            results.AddRange(Directory.GetDirectories(root)
                .SelectMany(categoryDirectory =>
                {
                    var category = Path.GetFileName(categoryDirectory);
                    return Directory.GetFiles(
                            categoryDirectory,
                            "*.png",
                            SearchOption.AllDirectories)
                        .Select(file => new Phase2IconTemplateDefinition(
                            category,
                            Path.GetFileNameWithoutExtension(file)
                                .Split("__", 2, StringSplitOptions.None)[0],
                            file));
                })
                .OrderBy(item => item.Category, StringComparer.Ordinal)
                .ThenBy(item => item.Id, StringComparer.Ordinal));
        }

        var imported = LoadImportedAssets(dataDirectory);
        results.AddRange(imported);
        var inventoryIds = results
            .Where(item => item.Category == "inventory-item")
            .SelectMany(item => item.CandidateIds ?? [item.Id])
            .ToHashSet(StringComparer.Ordinal);
        results.AddRange(imported
            .Where(item => item.Category == "special-item")
            .Where(item => (item.CandidateIds ?? [item.Id])
                .All(id => !inventoryIds.Contains(id)))
            .Select(item => item with
            {
                Category = "inventory-item",
                MinimumConfidence = 0.42,
                ComparisonMode = Phase2IconComparisonMode.InventoryAlphaMaskedColor,
                SemanticKind = "special-item"
            }));
        return results
            .OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, EquipmentTemplateKind>
        LoadEquipmentKinds(string equipmentIconDirectory)
    {
        var catalogPath = Path.GetFullPath(Path.Combine(
            equipmentIconDirectory,
            "..",
            "..",
            "equipment.json"));
        if (!File.Exists(catalogPath))
        {
            return new Dictionary<string, EquipmentTemplateKind>(
                StringComparer.Ordinal);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
            if (!document.RootElement.TryGetProperty("records", out var records) ||
                records.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, EquipmentTemplateKind>(
                    StringComparer.Ordinal);
            }

            var result = new Dictionary<string, EquipmentTemplateKind>(
                StringComparer.Ordinal);
            foreach (var record in records.EnumerateArray())
            {
                if (!record.TryGetProperty("id", out var idElement) ||
                    !record.TryGetProperty("category", out var categoryElement))
                {
                    continue;
                }

                var id = idElement.GetString();
                var category = categoryElement.GetString();
                if (string.IsNullOrWhiteSpace(id) ||
                    string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                var occupiesEquipmentSlot =
                    record.TryGetProperty(
                        "occupies_equipment_slot",
                        out var occupiesElement) &&
                    occupiesElement.ValueKind is JsonValueKind.True;
                result[id] = new EquipmentTemplateKind(
                    category,
                    occupiesEquipmentSlot);
            }

            return result;
        }
        catch (JsonException)
        {
            // A malformed catalog must degrade to Unknown recognition instead
            // of silently reverting to the ambiguous all-assets candidate set.
            return new Dictionary<string, EquipmentTemplateKind>(
                StringComparer.Ordinal);
        }
    }

    private sealed record EquipmentTemplateKind(
        string Category,
        bool OccupiesEquipmentSlot);

    private sealed record EquipmentTemplateSource(
        string Category,
        string Id,
        string FilePath,
        string Sha256,
        string SemanticKind,
        Phase2IconComparisonMode ComparisonMode);

    private static IReadOnlyList<Phase2IconTemplateDefinition>
        LoadImportedAssets(string dataDirectory)
    {
        var assetRoot = Path.Combine(dataDirectory, "phase2-icon-assets");
        var manifestPath = Path.Combine(assetRoot, "asset-manifest.jsonl");
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        var manifestInfo = new FileInfo(manifestPath);
        var overridesPath = Path.Combine(assetRoot, "recognition-overrides.json");
        var overridesInfo = File.Exists(overridesPath)
            ? new FileInfo(overridesPath)
            : null;
        if (ImportedCatalogs.TryGetValue(manifestInfo.FullName, out var cached) &&
            cached.LastWriteTimeUtc == manifestInfo.LastWriteTimeUtc &&
            cached.Length == manifestInfo.Length &&
            cached.OverridesLastWriteTimeUtc == overridesInfo?.LastWriteTimeUtc &&
            cached.OverridesLength == overridesInfo?.Length)
        {
            return cached.Templates;
        }

        var expectedVersion = Path.GetFileName(Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(dataDirectory)));
        var assets = File.ReadLines(manifestPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => ReadImportedAsset(line, assetRoot, expectedVersion))
            .Where(item => item.Available)
            .ToArray();

        var results = new List<Phase2IconTemplateDefinition>();
        results.AddRange(LoadSynergyIdentityTemplates(assets));
        results.AddRange(LoadVisualIdentityTemplates(
            assets,
            "enemy_affix",
            "negative-affix",
            0.62,
            includeDerived: true));
        results.AddRange(LoadVisualIdentityTemplates(
            assets,
            "investment_environment",
            "investment-environment",
            0.62,
            includeDerived: false));
        results.AddRange(LoadVisualIdentityTemplates(
            assets,
            "investment_strategy",
            "investment-strategy",
            0.62,
            includeDerived: false));
        results.AddRange(LoadSpecialItemTemplates(assets, dataDirectory));
        var loaded = ApplyRecognitionOverrides(results, assetRoot);
        ImportedCatalogs[manifestInfo.FullName] = new ImportedCatalogCache(
            manifestInfo.LastWriteTimeUtc,
            manifestInfo.Length,
            overridesInfo?.LastWriteTimeUtc,
            overridesInfo?.Length,
            loaded);
        return loaded;
    }

    private static IReadOnlyList<Phase2IconTemplateDefinition>
        ApplyRecognitionOverrides(
            IReadOnlyList<Phase2IconTemplateDefinition> templates,
            string assetRoot)
    {
        var path = Path.Combine(assetRoot, "recognition-overrides.json");
        if (!File.Exists(path))
        {
            return templates.ToArray();
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var version = RequiredString(document.RootElement, "gameVersion");
        var expectedVersion = Path.GetFileName(
            Directory.GetParent(assetRoot)?.FullName);
        if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"图标识别覆盖文件版本 {version} 与数据目录版本 {expectedVersion} 不一致。");
        }

        var byId = templates.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var entry in document.RootElement.GetProperty("entries")
                     .EnumerateArray())
        {
            var id = RequiredString(entry, "templateId");
            if (!byId.TryGetValue(id, out var template))
            {
                throw new InvalidDataException($"图标识别覆盖项不存在：{id}");
            }

            var enabled = entry.GetProperty("recognitionEnabled").GetBoolean();
            var reason = RequiredString(entry, "reason");
            byId[id] = template with
            {
                ResolvesExactIdentity = enabled,
                SourceConfidence = $"{template.SourceConfidence}; override: {reason}"
            };
        }

        return byId.Values.OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<Phase2IconTemplateDefinition>
        LoadSpecialItemTemplates(
            IReadOnlyList<ImportedIconAsset> assets,
            string dataDirectory)
    {
        var dataRoot = Directory.GetParent(dataDirectory)?.FullName;
        var equipmentManifest = dataRoot is null
            ? string.Empty
            : Path.Combine(
                dataRoot,
                "runtime",
                "1.0.0",
                Path.GetFileName(dataDirectory),
                "equipment",
                "equipment.json");
        var canonical = new Dictionary<string, (string Id, string Sha256)>(
            StringComparer.Ordinal);
        if (File.Exists(equipmentManifest))
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(equipmentManifest));
            foreach (var record in document.RootElement.GetProperty("records")
                         .EnumerateArray())
            {
                var name = RequiredString(record, "name");
                var id = RequiredString(record, "id");
                var sha256 = RequiredString(
                    record.GetProperty("icon"),
                    "sha256");
                canonical.Add(name, (id, sha256));
            }
        }

        var mapped = assets.Where(item => item.SourceCategory == "special_item")
            .Select(item =>
            {
                if (!canonical.TryGetValue(item.Name, out var existing))
                {
                    return item;
                }

                if (!string.Equals(
                        item.SourceSha256,
                        existing.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"特殊物品 {item.Name} 与现有装备库同名但图片版本冲突。");
                }

                return item with { Id = existing.Id };
            })
            .ToArray();
        var approved = mapped.Where(item =>
                !item.IsDerived ||
                item.Id is "special_item_020" or "special_item_021")
            .ToArray();
        var enabled = LoadVisualIdentityTemplates(
            approved,
            "special_item",
            "special-item",
            0.62,
            includeDerived: true,
            comparisonMode: Phase2IconComparisonMode.FullFrameColor)
            .Select(template => template.Id switch
            {
                "special_item_020" => template with { MinimumMargin = 0.010 },
                "special_item_021" => template with { MinimumMargin = 0.008 },
                _ => template
            })
            .ToArray();
        var derivedGuards = mapped.Where(item =>
                item.IsDerived &&
                item.Id is not "special_item_020" and not "special_item_021")
            .Select(item => new Phase2IconTemplateDefinition(
                "special-item",
                $"unverified_{item.Id}",
                item.FilePath,
                0.62,
                false,
                [item.Id],
                item.SourceConfidence,
                Phase2IconComparisonMode.FullFrameColor));
        return enabled.Concat(derivedGuards)
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static ImportedIconAsset ReadImportedAsset(
        string line,
        string assetRoot,
        string expectedVersion)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var version = RequiredString(root, "version");
        if (!string.Equals(version, expectedVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"图标资产版本 {version} 与数据目录版本 {expectedVersion} 不一致。");
        }

        var relativePath = RequiredString(root, "standardized_path");
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"图标资产路径必须是相对路径：{relativePath}");
        }

        var assetRootFull = Path.GetFullPath(assetRoot);
        var assetRootPrefix = Path.TrimEndingDirectorySeparator(assetRootFull) +
                              Path.DirectorySeparatorChar;
        var filePath = Path.GetFullPath(Path.Combine(
            assetRootFull,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!filePath.StartsWith(assetRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"图标资产路径越过资源目录：{relativePath}");
        }
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("图标资产文件缺失。", filePath);
        }

        return new ImportedIconAsset(
            RequiredString(root, "category"),
            RequiredString(root, "id"),
            RequiredString(root, "name"),
            RequiredString(root, "sha256"),
            filePath,
            RequiredString(root, "confidence"),
            root.TryGetProperty("available", out var available) &&
            available.ValueKind == JsonValueKind.True,
            root.TryGetProperty("derived_asset", out var derived) &&
            derived.ValueKind == JsonValueKind.True);
    }

    private static IReadOnlyList<Phase2IconTemplateDefinition>
        LoadSynergyIdentityTemplates(IReadOnlyList<ImportedIconAsset> assets) =>
        assets.Where(item => item.SourceCategory == "bond_state")
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group =>
            {
                var hashes = group.Select(item => item.SourceSha256)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (hashes.Length != 1)
                {
                    throw new InvalidDataException(
                        $"羁绊 {group.Key} 的身份模板并不唯一，不能安全合并激活层级。");
                }

                var source = group.First();
                return new Phase2IconTemplateDefinition(
                    "synergy",
                    $"bond_{group.Key}",
                    source.FilePath,
                    0.62,
                    true,
                    group.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray(),
                    source.SourceConfidence,
                    Phase2IconComparisonMode.ForegroundShape);
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<Phase2IconTemplateDefinition>
        LoadVisualIdentityTemplates(
            IReadOnlyList<ImportedIconAsset> assets,
            string sourceCategory,
            string targetCategory,
            double minimumConfidence,
            bool includeDerived,
            Phase2IconComparisonMode comparisonMode =
                Phase2IconComparisonMode.ForegroundShape) =>
        assets.Where(item => item.SourceCategory == sourceCategory &&
                             (includeDerived || !item.IsDerived))
            .GroupBy(
                item => VisualIdentityKey(item, comparisonMode),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray();
                var exact = ordered.Length == 1;
                var id = exact
                    ? ordered[0].Id
                    : $"visual_{sourceCategory}_{group.Key[..12]}";
                return new Phase2IconTemplateDefinition(
                    targetCategory,
                    id,
                    ordered[0].FilePath,
                    minimumConfidence,
                    exact,
                    ordered.Select(item => item.Id).ToArray(),
                    ordered[0].SourceConfidence,
                    comparisonMode);
            })
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToArray();

    private static string VisualIdentityKey(
        ImportedIconAsset asset,
        Phase2IconComparisonMode comparisonMode)
    {
        if (comparisonMode != Phase2IconComparisonMode.ForegroundShape)
        {
            return asset.SourceSha256;
        }

        using var source = Cv2.ImDecode(
            File.ReadAllBytes(asset.FilePath),
            ImreadModes.Unchanged);
        if (source.Empty())
        {
            throw new InvalidDataException($"图标模板无法读取：{asset.FilePath}");
        }

        using var normalized = OpenCvPhase2IconRecognizer.NormalizeForeground(
            source,
            useAlpha: true);
        if (normalized.Empty())
        {
            throw new InvalidDataException($"图标模板没有可识别前景：{asset.FilePath}");
        }

        Cv2.ImEncode(".png", normalized, out var bytes);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"图标资产清单缺少字段：{property}");
        }

        return value.GetString()!;
    }

    private sealed record ImportedIconAsset(
        string SourceCategory,
        string Id,
        string Name,
        string SourceSha256,
        string FilePath,
        string SourceConfidence,
        bool Available,
        bool IsDerived);

    private sealed record ImportedCatalogCache(
        DateTime LastWriteTimeUtc,
        long Length,
        DateTime? OverridesLastWriteTimeUtc,
        long? OverridesLength,
        IReadOnlyList<Phase2IconTemplateDefinition> Templates);
}

public sealed record Phase2IndicatorLocation(
    PixelRect Region,
    double Confidence,
    string TemplateId);

public static class Phase2ActionIndicatorLocator
{
    private const int TemplateReferenceWidth = 2559;

    public static Phase2IndicatorLocation? Locate(
        CaptureFrame frame,
        IReadOnlyList<Phase2IconTemplateDefinition> templates)
    {
        var candidates = LocateCandidates(
                frame,
                templates,
                maximumCandidates: 32)
            .Where(item => item.Confidence >= 0.35)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var bestConfidence = candidates.Max(item => item.Confidence);
        return candidates
            .Where(item => item.Confidence >= bestConfidence - 0.08)
            .OrderByDescending(item => item.Region.Y)
            .ThenByDescending(item => item.Confidence)
            .First();
    }

    public static IReadOnlyList<Phase2IndicatorLocation> LocateCandidates(
        CaptureFrame frame,
        IReadOnlyList<Phase2IconTemplateDefinition> templates,
        int maximumCandidates = 8)
    {
        if (maximumCandidates is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        var candidates = templates.Where(item => string.Equals(
                item.Category,
                "action-value-indicator",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        using var image = ToBgr(frame);
        // The colored remaining-action row moves along the complete left
        // timeline as combat advances. Search the whole timeline; the full
        // row template keeps the marker/hourglass context needed to reject
        // ordinary character entries with similar small digits.
        var searchBounds = new NormalizedRect(0.005, 0.035, 0.105, 0.720)
            .ToPixels(frame.Width, frame.Height);
        using var search = new Mat(
            image,
            new Rect(
                searchBounds.X,
                searchBounds.Y,
                searchBounds.Width,
                searchBounds.Height));
        var matches = new List<Phase2IndicatorLocation>();
        foreach (var candidate in candidates)
        {
            using var source = Cv2.ImDecode(
                File.ReadAllBytes(candidate.FilePath),
                ImreadModes.Color);
            if (source.Empty())
            {
                continue;
            }

            var iconWidth = source.Width;
            using var icon = new Mat(source, new Rect(0, 0, iconWidth, source.Height));
            var baseScale = frame.Width / (double)TemplateReferenceWidth;
            // The game UI scales with the captured 16:9 frame, which is
            // already accounted for by baseScale. Very large relative scales
            // (1.4/1.6) mostly matched an entire character entry and placed
            // its per-character countdown inside the action-value crop.
            foreach (var relativeScale in new[] { 0.80, 1.00, 1.20 })
            {
                var scale = baseScale * relativeScale;
                using var resized = new Mat();
                Cv2.Resize(
                    icon,
                    resized,
                    new Size(
                        Math.Max(4, (int)Math.Round(icon.Width * scale)),
                        Math.Max(4, (int)Math.Round(icon.Height * scale))),
                    interpolation: scale < 1
                        ? InterpolationFlags.Area
                        : InterpolationFlags.Cubic);
                if (resized.Width > search.Width || resized.Height > search.Height)
                {
                    continue;
                }

                using var scores = new Mat();
                Cv2.MatchTemplate(
                    search,
                    resized,
                    scores,
                    TemplateMatchModes.CCoeffNormed);
                for (var matchIndex = 0; matchIndex < 10; matchIndex++)
                {
                    Cv2.MinMaxLoc(
                        scores,
                        out _,
                        out var maximum,
                        out _,
                        out var location);
                    if (maximum < 0.25)
                    {
                        break;
                    }

                    // Full-row correlation can be deceptively high on an
                    // ordinary character entry because both rows contain a
                    // dark bar followed by small white digits. The leading
                    // hexagonal action marker is the discriminative part, so
                    // combine its local score with the row score before
                    // ranking candidates. This remains scale-independent and
                    // avoids OCR work on every timeline row.
                    var markerWidth = Math.Clamp(
                        (int)Math.Round(resized.Width * 0.22),
                        4,
                        resized.Width);
                    using var markerTemplate = new Mat(
                        resized,
                        new Rect(0, 0, markerWidth, resized.Height));
                    using var markerSample = new Mat(
                        search,
                        new Rect(
                            location.X,
                            location.Y,
                            markerWidth,
                            resized.Height));
                    using var markerScores = new Mat();
                    Cv2.MatchTemplate(
                        markerSample,
                        markerTemplate,
                        markerScores,
                        TemplateMatchModes.CCoeffNormed);
                    Cv2.MinMaxLoc(
                        markerScores,
                        out _,
                        out var markerMaximum,
                        out _,
                        out _);
                    var contextualConfidence =
                        (maximum * 0.45) +
                        (Math.Max(0, markerMaximum) * 0.55);

                    matches.Add(new Phase2IndicatorLocation(
                        new PixelRect(
                            searchBounds.X + location.X,
                            searchBounds.Y + location.Y,
                            resized.Width,
                            resized.Height),
                        contextualConfidence,
                        candidate.Id));

                    var suppression = new Rect(
                        Math.Max(0, location.X - resized.Width / 2),
                        Math.Max(0, location.Y - resized.Height / 2),
                        Math.Min(
                            scores.Width - Math.Max(0, location.X - resized.Width / 2),
                            resized.Width * 2),
                        Math.Min(
                            scores.Height - Math.Max(0, location.Y - resized.Height / 2),
                            resized.Height * 2));
                    if (suppression.Width > 0 && suppression.Height > 0)
                    {
                        using var suppressed = new Mat(scores, suppression);
                        suppressed.SetTo(new Scalar(-1));
                    }
                }
            }
        }

        var credible = matches.Where(item => item.Confidence >= 0.25).ToArray();
        if (credible.Length == 0)
        {
            return [];
        }

        // Character rows can occasionally produce a slightly better raw score
        // because they contain the same small digits and dark horizontal bar.
        // Prefer the strongest left-anchored visual evidence; the action row
        // itself can move anywhere along the timeline.
        var bestConfidence = credible.Max(item => item.Confidence);
        var leftAnchored = credible
            .Where(item => item.Region.X <= frame.Width * 0.045)
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.Region.Y);
        var ranked = leftAnchored
            .Concat(credible
            .Where(item => item.Confidence >= bestConfidence - 0.08)
            .OrderByDescending(item => item.Confidence)
            .ThenByDescending(item => item.Region.Y))
            .Concat(credible.OrderByDescending(item => item.Confidence))
            .ToArray();
        var distinct = new List<Phase2IndicatorLocation>(maximumCandidates);
        foreach (var candidate in ranked)
        {
            if (distinct.Any(existing => OverlapsSameRow(
                    existing.Region,
                    candidate.Region)))
            {
                continue;
            }

            distinct.Add(candidate);
            if (distinct.Count == maximumCandidates)
            {
                break;
            }
        }

        return distinct;
    }

    private static bool OverlapsSameRow(PixelRect left, PixelRect right)
    {
        var intersectionTop = Math.Max(left.Y, right.Y);
        var intersectionBottom = Math.Min(left.Bottom, right.Bottom);
        var smallerHeight = Math.Min(left.Height, right.Height);
        return smallerHeight > 0 &&
               intersectionBottom - intersectionTop >= smallerHeight * 0.55;
    }

    private static Mat ToBgr(CaptureFrame frame)
    {
        using var bgra = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        Marshal.Copy(frame.BgraPixels, 0, bgra.Data, frame.BgraPixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }
}

public sealed class OpenCvPhase2IconRecognizer :
    IPhase2IconRecognizer,
    IDisposable
{
    // Kept only for source compatibility with the retired exhaustive helper
    // below. Production recognition uses the pre-normalized cache instead.
    private readonly ConcurrentDictionary<string, Mat> _templates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Mat>> _preparedTemplates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Mat>> _preparedTemplateMasks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<TemplateShortlistDescriptor>>
        _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly int _candidateLimit;

    public OpenCvPhase2IconRecognizer(int candidateLimit = 64)
    {
        if (candidateLimit < 5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateLimit),
                "At least five candidates are required for traceable results.");
        }

        _candidateLimit = candidateLimit;
    }

    public int LastExactComparisonCount { get; private set; }

    /// <summary>
    /// Builds the bounded icon shortlist index before realtime collection.
    /// Unknown-result and confidence behavior remain unchanged; only immutable
    /// template decoding is moved out of the first-frame path.
    /// </summary>
    public Task WarmUpAsync(
        IReadOnlyList<Phase2IconTemplateDefinition> templates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(templates);
        return Task.Run(
            () => Parallel.ForEach(
                templates
                    .GroupBy(
                        item => $"{item.ComparisonMode}|{item.FilePath}",
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First()),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = 4
                },
                template => _ = LoadDescriptor(template)),
            cancellationToken);
    }

    public IReadOnlyList<Phase2IconRecognition> Recognize(
        CaptureFrame frame,
        string category,
        IReadOnlyList<NormalizedRect> slots,
        IReadOnlyList<Phase2IconTemplateDefinition> templates)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var candidates = templates
            .Where(item => string.Equals(
                item.Category,
                category,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        using var image = ToBgr(frame);
        var results = new List<Phase2IconRecognition>(slots.Count);
        for (var index = 0; index < slots.Count; index++)
        {
            var region = slots[index].ToPixels(frame.Width, frame.Height);
            if (region.IsEmpty || candidates.Length == 0)
            {
                results.Add(new Phase2IconRecognition(
                    index,
                    region,
                    null,
                    0,
                    false));
                continue;
            }

            using var crop = new Mat(
                image,
                new Rect(region.X, region.Y, region.Width, region.Height));
            var preparedCrops = new Dictionary<Phase2IconComparisonMode, Mat>();
            (Phase2IconTemplateDefinition Template, double Confidence)[] ranked;
            try
            {
                var shortlisted = candidates
                    .GroupBy(template => template.ComparisonMode)
                    .SelectMany(group => Shortlist(
                        group,
                        preparedCrops,
                        crop))
                    .ToArray();
                LastExactComparisonCount = shortlisted.Length;
                ranked = shortlisted
                    .Select(template =>
                    {
                        if (!preparedCrops.TryGetValue(
                                template.ComparisonMode,
                                out var preparedCrop))
                        {
                            preparedCrop = Prepare(
                                crop,
                                template.ComparisonMode,
                                useAlpha: false);
                            preparedCrops.Add(
                                template.ComparisonMode,
                                preparedCrop);
                        }

                        return (
                            Template: template,
                            Confidence: ComparePrepared(
                                preparedCrop,
                                LoadPrepared(template),
                                template.ComparisonMode is
                                    Phase2IconComparisonMode.AlphaMaskedColor
                                    ? LoadPreparedMask(template)
                                    : null,
                                template.ComparisonMode));
                    })
                    .OrderByDescending(item => item.Confidence)
                    .ToArray();
            }
            finally
            {
                foreach (var preparedCrop in preparedCrops.Values)
                {
                    preparedCrop.Dispose();
                }
            }
            var best = ranked[0];
            var margin = ranked.Length > 1
                ? best.Confidence - ranked[1].Confidence
                : best.Confidence;
            var categoryMinimumMargin = string.Equals(
                category,
                "character-avatar",
                StringComparison.OrdinalIgnoreCase)
                ? 0.010
                : 0.025;
            var minimumMargin = best.Template.MinimumMargin ??
                                categoryMinimumMargin;
            var known = best.Confidence >= best.Template.MinimumConfidence &&
                        margin >= minimumMargin &&
                        best.Template.ResolvesExactIdentity;
            var candidateTemplateIds = known ||
                                       !best.Template.ResolvesExactIdentity
                ? best.Template.CandidateIds ?? [best.Template.Id]
                : ranked
                    .Where(item =>
                        best.Confidence - item.Confidence <= minimumMargin)
                    .SelectMany(item =>
                        item.Template.CandidateIds ?? [item.Template.Id])
                    .Distinct(StringComparer.Ordinal)
                    .Take(8)
                    .ToArray();
            results.Add(new Phase2IconRecognition(
                index,
                region,
                // Preserve the best candidate even when it is below the
                // acceptance/margin gate. Callers may persist it as uncertain
                // evidence, but must consult IsKnown before using it as data.
                best.Template.Id,
                best.Confidence,
                known,
                candidateTemplateIds,
                ranked.Take(5).Select(item => new Phase2IconCandidate(
                    item.Template.Id,
                    item.Confidence,
                    item.Template.ResolvesExactIdentity,
                    item.Template.CandidateIds ?? [item.Template.Id])).ToArray()));
        }

        return results;
    }

    public void Dispose()
    {
        foreach (var template in _preparedTemplates.Values)
        {
            if (template.IsValueCreated)
            {
                template.Value.Dispose();
            }
        }

        _preparedTemplates.Clear();
        foreach (var mask in _preparedTemplateMasks.Values)
        {
            if (mask.IsValueCreated)
            {
                mask.Value.Dispose();
            }
        }

        _preparedTemplateMasks.Clear();
        _descriptors.Clear();
    }

    private Mat Load(string path) => _templates.GetOrAdd(
        path,
        static file =>
        {
            var image = Cv2.ImDecode(File.ReadAllBytes(file), ImreadModes.Unchanged);
            if (image.Empty())
            {
                image.Dispose();
                throw new InvalidDataException($"图标模板无法读取：{file}");
            }

            return image;
        });

    private static double Compare(
        Mat crop,
        Mat template,
        Phase2IconComparisonMode comparisonMode)
    {
        if (comparisonMode == Phase2IconComparisonMode.ForegroundShape)
        {
            using var cropMask = NormalizeForeground(crop, useAlpha: false);
            using var templateMask = NormalizeForeground(template, useAlpha: true);
            if (cropMask.Empty() || templateMask.Empty())
            {
                return -1;
            }

            using var shapeScore = new Mat();
            Cv2.MatchTemplate(
                cropMask,
                templateMask,
                shapeScore,
                TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(shapeScore, out _, out var shapeMaximum, out _, out _);
            return shapeMaximum;
        }

        const int size = 64;
        using var normalizedCrop = new Mat();
        using var normalizedTemplate = new Mat();
        Cv2.Resize(crop, normalizedCrop, new Size(size, size));
        Cv2.Resize(template, normalizedTemplate, new Size(size, size));
        if (comparisonMode is Phase2IconComparisonMode.FullFrameColor or
            Phase2IconComparisonMode.AlphaMaskedColor or
            Phase2IconComparisonMode.InventoryAlphaMaskedColor)
        {
            using var templateBgr = new Mat();
            if (normalizedTemplate.Channels() == 4)
            {
                Cv2.CvtColor(
                    normalizedTemplate,
                    templateBgr,
                    ColorConversionCodes.BGRA2BGR);
            }
            else
            {
                normalizedTemplate.CopyTo(templateBgr);
            }

            using var colorScore = new Mat();
            Cv2.MatchTemplate(
                normalizedCrop,
                templateBgr,
                colorScore,
                TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(colorScore, out _, out var colorMaximum, out _, out _);
            return colorMaximum;
        }

        using var cropGray = new Mat();
        using var templateGray = new Mat();
        Cv2.CvtColor(normalizedCrop, cropGray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(
            normalizedTemplate,
            templateGray,
            normalizedTemplate.Channels() == 4
                ? ColorConversionCodes.BGRA2GRAY
                : ColorConversionCodes.BGR2GRAY);
        using var score = new Mat();
        Cv2.MatchTemplate(cropGray, templateGray, score, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(score, out _, out var maximum, out _, out _);
        return maximum;
    }

    private IEnumerable<Phase2IconTemplateDefinition> Shortlist(
        IEnumerable<Phase2IconTemplateDefinition> source,
        IDictionary<Phase2IconComparisonMode, Mat> preparedCrops,
        Mat crop)
    {
        var candidates = source.ToArray();
        if (candidates.Length <= _candidateLimit)
        {
            return candidates;
        }

        var mode = candidates[0].ComparisonMode;
        if (!preparedCrops.TryGetValue(mode, out var preparedCrop))
        {
            preparedCrop = Prepare(crop, mode, useAlpha: false);
            preparedCrops.Add(mode, preparedCrop);
        }

        if (preparedCrop.Empty())
        {
            return candidates
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Take(_candidateLimit)
                .ToArray();
        }

        var query = CreateShortlistDescriptor(preparedCrop);
        return candidates
            .Select(template => (
                Template: template,
                Similarity: ShortlistSimilarity(
                    query,
                    LoadDescriptor(template),
                    mode)))
            .OrderByDescending(item => item.Similarity)
            .ThenBy(item => item.Template.Id, StringComparer.Ordinal)
            .Take(_candidateLimit)
            .Select(item => item.Template)
            .ToArray();
    }

    private TemplateShortlistDescriptor LoadDescriptor(
        Phase2IconTemplateDefinition template) =>
        _descriptors.GetOrAdd(
            $"{template.ComparisonMode}|{template.FilePath}",
            _ => new Lazy<TemplateShortlistDescriptor>(
                () => CreateShortlistDescriptor(LoadPrepared(template)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static TemplateShortlistDescriptor CreateShortlistDescriptor(
        Mat source)
    {
        if (source.Empty())
        {
            return new TemplateShortlistDescriptor([], 0, []);
        }

        return new TemplateShortlistDescriptor(
            CreateCompactDescriptor(source),
            ComputePerceptualHash(source),
            source.Channels() == 3 ? CreateColorHistogram(source) : []);
    }

    private static float[] CreateCompactDescriptor(Mat source)
    {
        if (source.Empty())
        {
            return [];
        }

        // 48 px retains thin equipment silhouettes and border details that
        // collapse at 32 px.  The descriptor remains far cheaper than the
        // exact alpha-masked comparison and is cached per template.
        const int side = 48;
        using var reduced = new Mat();
        Cv2.Resize(
            source,
            reduced,
            new Size(side, side),
            interpolation: InterpolationFlags.Area);
        var length = checked(side * side * reduced.Channels());
        var pixels = new byte[length];
        Marshal.Copy(reduced.Data, pixels, 0, pixels.Length);
        var mean = pixels.Average(value => (double)value);
        var descriptor = new float[pixels.Length];
        var squaredNorm = 0d;
        for (var index = 0; index < pixels.Length; index++)
        {
            var centered = pixels[index] - mean;
            descriptor[index] = (float)centered;
            squaredNorm += centered * centered;
        }

        if (squaredNorm <= double.Epsilon)
        {
            return descriptor;
        }

        var inverseNorm = 1d / Math.Sqrt(squaredNorm);
        for (var index = 0; index < descriptor.Length; index++)
        {
            descriptor[index] = (float)(descriptor[index] * inverseNorm);
        }

        return descriptor;
    }

    private static double DescriptorSimilarity(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return double.NegativeInfinity;
        }

        var score = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            score += left[index] * right[index];
        }

        return score;
    }

    private static double ShortlistSimilarity(
        TemplateShortlistDescriptor query,
        TemplateShortlistDescriptor candidate,
        Phase2IconComparisonMode comparisonMode)
    {
        var compact = DescriptorSimilarity(query.Compact, candidate.Compact);
        var perceptual = 1d - BitOperations.PopCount(
            query.PerceptualHash ^ candidate.PerceptualHash) / 64d;
        return comparisonMode switch
        {
            Phase2IconComparisonMode.AlphaMaskedColor =>
                perceptual * 0.65 + compact * 0.35,
            Phase2IconComparisonMode.InventoryAlphaMaskedColor =>
                HistogramSimilarity(query.ColorHistogram, candidate.ColorHistogram) * 0.45 +
                compact * 0.35 +
                perceptual * 0.20,
            _ => compact
        };
    }

    private static double HistogramSimilarity(
        IReadOnlyList<double> left,
        IReadOnlyList<double> right)
    {
        if (left.Count == 0 || left.Count != right.Count)
        {
            return 0;
        }

        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        return leftNorm <= double.Epsilon || rightNorm <= double.Epsilon
            ? 0
            : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private sealed record TemplateShortlistDescriptor(
        float[] Compact,
        ulong PerceptualHash,
        double[] ColorHistogram);

    private Mat LoadPrepared(Phase2IconTemplateDefinition template) =>
        _preparedTemplates.GetOrAdd(
            $"{template.ComparisonMode}|{template.FilePath}",
            _ => new Lazy<Mat>(
                () =>
                {
                    using var image = Cv2.ImDecode(
                        File.ReadAllBytes(template.FilePath),
                        ImreadModes.Unchanged);
                    if (image.Empty())
                    {
                        throw new InvalidDataException(
                            $"Icon template could not be decoded: {template.FilePath}");
                    }

                    return Prepare(
                        image,
                        template.ComparisonMode,
                        useAlpha: true);
                },
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private Mat LoadPreparedMask(Phase2IconTemplateDefinition template) =>
        _preparedTemplateMasks.GetOrAdd(
            template.FilePath,
            _ => new Lazy<Mat>(
                () =>
                {
                    using var image = Cv2.ImDecode(
                        File.ReadAllBytes(template.FilePath),
                        ImreadModes.Unchanged);
                    if (image.Empty())
                    {
                        throw new InvalidDataException(
                            $"Icon template mask could not be decoded: {template.FilePath}");
                    }

                    using var alpha = new Mat();
                    if (image.Channels() == 4)
                    {
                        Cv2.ExtractChannel(image, alpha, 3);
                    }
                    else
                    {
                        alpha.Create(image.Rows, image.Cols, MatType.CV_8UC1);
                        alpha.SetTo(Scalar.All(255));
                    }

                    var resized = new Mat();
                    Cv2.Resize(
                        alpha,
                        resized,
                        new Size(64, 64),
                        interpolation: InterpolationFlags.Area);
                    Cv2.Threshold(resized, resized, 16, 255, ThresholdTypes.Binary);
                    // Stack counts are drawn over the lower-left of inventory
                    // and character equipment icons. Exclude that overlay-only
                    // area from identity matching; the remaining upper and
                    // right portions still contain the stable item artwork.
                    using (var quantityOverlay = new Mat(
                               resized,
                               new Rect(0, 28, 30, 36)))
                    {
                        quantityOverlay.SetTo(Scalar.All(0));
                    }
                    return resized;
                },
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static Mat Prepare(
        Mat source,
        Phase2IconComparisonMode comparisonMode,
        bool useAlpha)
    {
        if (comparisonMode == Phase2IconComparisonMode.ForegroundShape)
        {
            return NormalizeForeground(source, useAlpha);
        }

        if (comparisonMode ==
            Phase2IconComparisonMode.InventoryAlphaMaskedColor)
        {
            return NormalizeInventoryArtwork(source, useAlpha);
        }

        const int size = 64;
        using var bgr = new Mat();
        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            source.CopyTo(bgr);
        }

        var resized = new Mat();
        if (comparisonMode == Phase2IconComparisonMode.AlphaMaskedColor &&
            !useAlpha)
        {
            // Equipment is rendered as a square icon at the upper-left of a
            // slightly larger logical slot. The remainder contains blue board
            // background and may include a quantity badge. Comparing that
            // whole slot against a square source asset destroys alignment, so
            // normalize only the stable icon square. This is category-local;
            // other icon families retain their existing preparation path.
            var stableFraction = comparisonMode ==
                                 Phase2IconComparisonMode.AlphaMaskedColor
                ? 0.80
                : 1.0;
            var side = Math.Clamp(
                (int)Math.Round(
                    Math.Min(bgr.Width, bgr.Height) * stableFraction),
                4,
                Math.Min(bgr.Width, bgr.Height));
            using var iconSquare = new Mat(bgr, new Rect(0, 0, side, side));
            Cv2.Resize(iconSquare, resized, new Size(size, size));
        }
        else
        {
            Cv2.Resize(bgr, resized, new Size(size, size));
        }
        if (comparisonMode is Phase2IconComparisonMode.FullFrameColor or
            Phase2IconComparisonMode.AlphaMaskedColor)
        {
            return resized;
        }

        var gray = new Mat();
        Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
        resized.Dispose();
        return gray;
    }

    private static double ComparePrepared(
        Mat crop,
        Mat template,
        Mat? mask,
        Phase2IconComparisonMode comparisonMode)
    {
        if (crop.Empty() || template.Empty())
        {
            return -1;
        }

        using var score = new Mat();
        if (mask is null)
        {
            Cv2.MatchTemplate(
                crop,
                template,
                score,
                TemplateMatchModes.CCoeffNormed);
        }
        else
        {
            Cv2.MatchTemplate(
                crop,
                template,
                score,
                TemplateMatchModes.CCorrNormed,
                mask);
        }
        Cv2.MinMaxLoc(score, out _, out var maximum, out _, out _);
        if (mask is null && comparisonMode !=
            Phase2IconComparisonMode.InventoryAlphaMaskedColor)
        {
            return maximum;
        }

        var perceptualSimilarity = ComparePerceptualHash(crop, template);
        return comparisonMode == Phase2IconComparisonMode.InventoryAlphaMaskedColor
            ? CompareColorHistogram(crop, template) * 0.45 +
              maximum * 0.35 +
              perceptualSimilarity * 0.20
            : perceptualSimilarity * 0.65 + maximum * 0.35;
    }

    private static double CompareColorHistogram(Mat left, Mat right)
    {
        var leftHistogram = CreateColorHistogram(left);
        var rightHistogram = CreateColorHistogram(right);
        var dot = 0d;
        var leftNorm = 0d;
        var rightNorm = 0d;
        for (var index = 0; index < leftHistogram.Length; index++)
        {
            dot += leftHistogram[index] * rightHistogram[index];
            leftNorm += leftHistogram[index] * leftHistogram[index];
            rightNorm += rightHistogram[index] * rightHistogram[index];
        }

        return leftNorm <= double.Epsilon || rightNorm <= double.Epsilon
            ? 0
            : dot / Math.Sqrt(leftNorm * rightNorm);
    }

    private static double[] CreateColorHistogram(Mat source)
    {
        const int hueBins = 12;
        const int saturationBins = 4;
        const int valueBins = 4;
        var histogram = new double[hueBins * saturationBins * valueBins];
        using var hsv = new Mat();
        Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
        for (var y = 0; y < hsv.Rows; y++)
        {
            for (var x = 0; x < hsv.Cols; x++)
            {
                var pixel = hsv.At<Vec3b>(y, x);
                if (pixel.Item1 < 12 && pixel.Item2 < 45 || pixel.Item2 < 28)
                {
                    continue;
                }

                var hue = Math.Min(hueBins - 1, pixel.Item0 * hueBins / 180);
                var saturation = Math.Min(
                    saturationBins - 1,
                    pixel.Item1 * saturationBins / 256);
                var value = Math.Min(
                    valueBins - 1,
                    pixel.Item2 * valueBins / 256);
                histogram[(hue * saturationBins + saturation) * valueBins + value]++;
            }
        }

        return histogram;
    }

    private static double ComparePerceptualHash(
        Mat left,
        Mat right,
        Mat? mask = null)
    {
        if (mask is null)
        {
            return 1d - BitOperations.PopCount(
                ComputePerceptualHash(left) ^
                ComputePerceptualHash(right)) / 64d;
        }

        using var maskedLeft = Mat.Zeros(left.Rows, left.Cols, left.Type()).ToMat();
        using var maskedRight = Mat.Zeros(right.Rows, right.Cols, right.Type()).ToMat();
        left.CopyTo(maskedLeft, mask);
        right.CopyTo(maskedRight, mask);
        var leftHash = ComputePerceptualHash(maskedLeft);
        var rightHash = ComputePerceptualHash(maskedRight);
        return 1d - BitOperations.PopCount(leftHash ^ rightHash) / 64d;
    }

    private static ulong ComputePerceptualHash(Mat source)
    {
        using var gray = new Mat();
        if (source.Channels() == 1)
        {
            source.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        }

        // The game draws stack counts over the lower-left corner. Replacing
        // that quadrant with the image mean keeps the hash focused on the
        // stable artwork shared with the source asset.
        using (var overlay = new Mat(gray, new Rect(
                   0,
                   gray.Rows * 7 / 16,
                   gray.Cols * 15 / 32,
                   gray.Rows - gray.Rows * 7 / 16)))
        {
            overlay.SetTo(Cv2.Mean(gray));
        }

        using var reduced = new Mat();
        Cv2.Resize(gray, reduced, new Size(32, 32), interpolation: InterpolationFlags.Area);
        using var floating = new Mat();
        reduced.ConvertTo(floating, MatType.CV_32FC1);
        using var coefficients = new Mat();
        Cv2.Dct(floating, coefficients);
        var values = new float[64];
        var cursor = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                values[cursor++] = coefficients.At<float>(y, x);
            }
        }

        var median = values.Skip(1).OrderBy(value => value).ElementAt(31);
        ulong hash = 0;
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] >= median)
            {
                hash |= 1UL << index;
            }
        }

        return hash;
    }

    private static Mat NormalizeInventoryArtwork(Mat source, bool useAlpha)
    {
        const int size = 64;
        const int padding = 4;
        using var bgr = new Mat();
        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            source.CopyTo(bgr);
        }

        using var mask = new Mat();
        if (useAlpha && source.Channels() == 4)
        {
            using var alpha = new Mat();
            Cv2.ExtractChannel(source, alpha, 3);
            Cv2.Threshold(alpha, mask, 16, 255, ThresholdTypes.Binary);
        }
        else
        {
            // Runtime inventory artwork is centered inside a dark square.
            // Remove the frame first, then isolate non-background artwork.
            // This makes matching invariant to the browser/client border and
            // to small DPI-dependent padding differences.
            using var hsv = new Mat();
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
            Cv2.InRange(
                hsv,
                new Scalar(0, 0, 72),
                new Scalar(180, 255, 255),
                mask);
            var border = Math.Max(1, Math.Min(mask.Width, mask.Height) / 9);
            mask.RowRange(0, border).SetTo(Scalar.All(0));
            mask.RowRange(mask.Rows - border, mask.Rows).SetTo(Scalar.All(0));
            mask.ColRange(0, border).SetTo(Scalar.All(0));
            mask.ColRange(mask.Cols - border, mask.Cols).SetTo(Scalar.All(0));
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Ellipse,
                new Size(3, 3));
            Cv2.MorphologyEx(mask, mask, MorphTypes.Open, kernel);
        }

        if (Cv2.CountNonZero(mask) < 8)
        {
            return new Mat();
        }

        // Ignore stack-count text at the lower-left; it is not part of the
        // item identity and may change between frames.
        using (var quantity = new Mat(
                   mask,
                   new Rect(
                       0,
                       mask.Rows * 2 / 3,
                       mask.Cols * 2 / 5,
                       mask.Rows - mask.Rows * 2 / 3)))
        {
            quantity.SetTo(Scalar.All(0));
        }

        var bounds = Cv2.BoundingRect(mask);
        using var artwork = new Mat(bgr, bounds);
        using var artworkMask = new Mat(mask, bounds);
        using var isolated = Mat.Zeros(
            artwork.Rows,
            artwork.Cols,
            artwork.Type()).ToMat();
        artwork.CopyTo(isolated, artworkMask);

        var targetExtent = size - padding * 2;
        var scale = Math.Min(
            targetExtent / (double)isolated.Width,
            targetExtent / (double)isolated.Height);
        var width = Math.Max(1, (int)Math.Round(isolated.Width * scale));
        var height = Math.Max(1, (int)Math.Round(isolated.Height * scale));
        using var resized = new Mat();
        Cv2.Resize(isolated, resized, new Size(width, height));
        var normalized = Mat.Zeros(size, size, MatType.CV_8UC3).ToMat();
        using var destination = new Mat(
            normalized,
            new Rect((size - width) / 2, (size - height) / 2, width, height));
        resized.CopyTo(destination);
        return normalized;
    }

    internal static Mat NormalizeForeground(Mat source, bool useAlpha)
    {
        const int size = 64;
        const int padding = 4;
        using var bgr = new Mat();
        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            source.CopyTo(bgr);
        }

        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var mask = new Mat();
        Cv2.InRange(
            hsv,
            new Scalar(0, 0, 150),
            new Scalar(180, 125, 255),
            mask);
        if (useAlpha && source.Channels() == 4)
        {
            using var alpha = new Mat();
            Cv2.ExtractChannel(source, alpha, 3);
            using var alphaMask = new Mat();
            Cv2.Threshold(alpha, alphaMask, 16, 255, ThresholdTypes.Binary);
            Cv2.BitwiseAnd(mask, alphaMask, mask);
        }

        if (Cv2.CountNonZero(mask) < 8)
        {
            return new Mat();
        }

        var bounds = Cv2.BoundingRect(mask);
        using var foreground = new Mat(mask, bounds);
        var targetExtent = size - padding * 2;
        var scale = Math.Min(
            targetExtent / (double)foreground.Width,
            targetExtent / (double)foreground.Height);
        var width = Math.Max(1, (int)Math.Round(foreground.Width * scale));
        var height = Math.Max(1, (int)Math.Round(foreground.Height * scale));
        using var resized = new Mat();
        Cv2.Resize(
            foreground,
            resized,
            new Size(width, height),
            interpolation: InterpolationFlags.Nearest);
        var normalized = Mat.Zeros(size, size, MatType.CV_8UC1).ToMat();
        var x = (size - width) / 2;
        var y = (size - height) / 2;
        using var destination = new Mat(normalized, new Rect(x, y, width, height));
        resized.CopyTo(destination);
        return normalized;
    }

    private static Mat ToBgr(CaptureFrame frame)
    {
        using var bgra = new Mat(frame.Height, frame.Width, MatType.CV_8UC4);
        Marshal.Copy(frame.BgraPixels, 0, bgra.Data, frame.BgraPixels.Length);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }
}
