using System.Runtime.InteropServices;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Vision;
using OpenCvSharp;

namespace CurrencyWarsAssistant.Tests;

public sealed class SpecialEquipmentCandidateCatalogTests
{
    [Theory]
    [InlineData(
        "currency_wars_equipment_027",
        "currency_wars_equipment_028",
        "currency_wars_equipment_029")]
    [InlineData(
        "currency_wars_equipment_034",
        "currency_wars_equipment_035")]
    public void Load_RuntimeSilverWolfVariants_ExposeOneAmbiguousSpecialItemGroup(
        params string[] expectedCandidateIds)
    {
        var templates = Phase2IconTemplateCatalog.Load(DataDirectory);

        var template = Assert.Single(templates.Where(item =>
            item.Category == "special-item" &&
            expectedCandidateIds.All(id =>
                (item.CandidateIds ?? []).Contains(id, StringComparer.Ordinal))));

        Assert.False(template.ResolvesExactIdentity);
        Assert.Equal(expectedCandidateIds, template.CandidateIds);
        Assert.Equal("special-item", template.SemanticKind);
        Assert.Equal(
            expectedCandidateIds[0] == "currency_wars_equipment_027"
                ? ["数据拷贝仪", "数据拷贝仪Max", "数据拷贝仪Pro"]
                : ["病毒防火墙", "病毒防火墙Max"],
            template.CandidateDisplayNames);
        Assert.Equal(
            Phase2IconComparisonMode.InventoryAlphaMaskedColor,
            template.ComparisonMode);
    }

    [Fact]
    public void Load_AllSilverWolfHackingComponents_PreserveEightVisualGroups()
    {
        var expectedGroups = new[]
        {
            new[] { "currency_wars_equipment_022", "currency_wars_equipment_023" },
            ["currency_wars_equipment_024", "currency_wars_equipment_025", "currency_wars_equipment_026"],
            ["currency_wars_equipment_027", "currency_wars_equipment_028", "currency_wars_equipment_029"],
            ["currency_wars_equipment_030", "currency_wars_equipment_031"],
            ["currency_wars_equipment_032", "currency_wars_equipment_033"],
            ["currency_wars_equipment_034", "currency_wars_equipment_035"],
            ["currency_wars_equipment_036"],
            ["currency_wars_equipment_037"]
        };
        var templates = Phase2IconTemplateCatalog.Load(DataDirectory)
            .Where(item => item.Category == "special-item" &&
                           (item.CandidateIds ?? []).Any(id =>
                               string.CompareOrdinal(
                                   id,
                                   "currency_wars_equipment_022") >= 0 &&
                               string.CompareOrdinal(
                                   id,
                                   "currency_wars_equipment_037") <= 0))
            .ToArray();

        Assert.Equal(expectedGroups.Length, templates.Length);
        foreach (var expectedGroup in expectedGroups)
        {
            var template = Assert.Single(templates.Where(item =>
                (item.CandidateIds ?? []).SequenceEqual(expectedGroup)));
            Assert.Equal(expectedGroup.Length == 1, template.ResolvesExactIdentity);
            Assert.Equal(expectedGroup.Length, template.CandidateDisplayNames?.Count);
        }
    }

    [Fact]
    public void Load_FateAndCursedVariants_RemainReadableAdvancedEquipmentGroups()
    {
        var expectedGroups = new[]
        {
            new[] { "currency_wars_equipment_002", "currency_wars_equipment_003", "currency_wars_equipment_013" },
            ["currency_wars_equipment_004", "currency_wars_equipment_007", "currency_wars_equipment_014"],
            ["currency_wars_equipment_005", "currency_wars_equipment_008", "currency_wars_equipment_015"],
            ["currency_wars_equipment_006", "currency_wars_equipment_009", "currency_wars_equipment_016"],
            ["currency_wars_equipment_010", "currency_wars_equipment_017", "currency_wars_equipment_020"]
        };
        var templates = Phase2IconTemplateCatalog.Load(DataDirectory);

        foreach (var expectedGroup in expectedGroups)
        {
            var template = Assert.Single(templates.Where(item =>
                item.Category == "advanced-equipment" &&
                (item.CandidateIds ?? []).SequenceEqual(expectedGroup)));
            Assert.False(template.ResolvesExactIdentity);
            Assert.Equal(expectedGroup.Length, template.CandidateDisplayNames?.Count);
            Assert.DoesNotContain(templates, item =>
                item.Category == "special-item" &&
                (item.CandidateIds ?? []).SequenceEqual(expectedGroup));
        }
    }

    [Fact]
    public void Recognize_VirusFirewallVisual_ReturnsUnknownReadableCandidateGroup()
    {
        var templates = Phase2IconTemplateCatalog.Load(DataDirectory);
        var virusFirewall = Assert.Single(templates.Where(item =>
            item.Category == "special-item" &&
            (item.CandidateIds ?? []).SequenceEqual(new[]
            {
                "currency_wars_equipment_034",
                "currency_wars_equipment_035"
            })));
        var frame = LoadTemplateOnOpaqueBackground(virusFirewall.FilePath);
        using var recognizer = new OpenCvPhase2IconRecognizer();

        var result = Assert.Single(recognizer.Recognize(
            frame,
            "special-item",
            [new NormalizedRect(0, 0, 1, 1)],
            templates));

        Assert.False(result.IsKnown);
        Assert.Equal(virusFirewall.CandidateIds, result.CandidateTemplateIds);
        Assert.Equal(
            ["病毒防火墙", "病毒防火墙Max"],
            result.CandidateDisplayNames);
    }

    [Fact]
    public void Recognize_DataCopierVisual_ReturnsUnknownReadableCandidateGroup()
    {
        var templates = Phase2IconTemplateCatalog.Load(DataDirectory);
        var dataCopier = Assert.Single(templates.Where(item =>
            item.Category == "special-item" &&
            (item.CandidateIds ?? []).SequenceEqual(new[]
            {
                "currency_wars_equipment_027",
                "currency_wars_equipment_028",
                "currency_wars_equipment_029"
            })));
        var frame = LoadTemplateOnOpaqueBackground(dataCopier.FilePath);
        using var recognizer = new OpenCvPhase2IconRecognizer();

        var result = Assert.Single(recognizer.Recognize(
            frame,
            "special-item",
            [new NormalizedRect(0, 0, 1, 1)],
            templates));

        Assert.False(result.IsKnown);
        Assert.Equal(dataCopier.CandidateIds, result.CandidateTemplateIds);
        Assert.Equal(
            ["数据拷贝仪", "数据拷贝仪Max", "数据拷贝仪Pro"],
            result.CandidateDisplayNames);
    }

    [Fact]
    public void Load_PrivilegedNormalEquipmentContract_RemainsAdvancedOnly()
    {
        var templates = Phase2IconTemplateCatalog.Load(DataDirectory);
        var firestormGroup = new[]
        {
            "currency_wars_equipment_066",
            "currency_wars_equipment_105"
        };

        Assert.Single(templates.Where(item =>
            item.Category == "advanced-equipment" &&
            (item.CandidateIds ?? []).SequenceEqual(firestormGroup)));
        Assert.DoesNotContain(templates, item =>
            item.Category == "special-item" &&
            (item.CandidateIds ?? []).SequenceEqual(firestormGroup));
    }

    private static CaptureFrame LoadTemplateOnOpaqueBackground(string path)
    {
        using var source = Cv2.ImDecode(
            File.ReadAllBytes(path),
            ImreadModes.Unchanged);
        Assert.False(source.Empty(), path);
        using var bgr = new Mat(
            source.Height,
            source.Width,
            MatType.CV_8UC3,
            new Scalar(12, 12, 18));
        if (source.Channels() == 4)
        {
            using var sourceBgr = new Mat();
            Cv2.CvtColor(source, sourceBgr, ColorConversionCodes.BGRA2BGR);
            using var alpha = new Mat();
            Cv2.ExtractChannel(source, alpha, 3);
            sourceBgr.CopyTo(bgr, alpha);
        }
        else
        {
            source.CopyTo(bgr);
        }

        using var bgra = new Mat();
        Cv2.CvtColor(bgr, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[checked((int)(bgra.Total() * bgra.ElemSize()))];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new CaptureFrame(
            bgra.Width,
            bgra.Height,
            bgra.Width * 4,
            pixels,
            new PixelRect(0, 0, bgra.Width, bgra.Height),
            DateTimeOffset.UtcNow);
    }

    private static string DataDirectory => Path.Combine(
        RepositoryRoot,
        "data",
        "4.4");

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));
}
