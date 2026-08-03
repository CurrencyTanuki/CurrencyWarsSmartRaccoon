using CurrencyWarsAssistant.Advisor;
using CurrencyWarsAssistant.Core;
using CurrencyWarsAssistant.Game;
using CurrencyWarsAssistant.Tasks;
using CurrencyWarsAssistant.Vision;
using Xunit.Abstractions;

namespace CurrencyWarsAssistant.Tests;

public sealed class NegativeAffixProductionRecognitionTests(
    ITestOutputHelper output)
{
    [Theory]
    [InlineData(
        "run-171955-preparation-1-3-stable.png",
        "NegativeAffix-2",
        "enemy_affix_t2_01")]
    [InlineData("preparation-1-4-user-2026-08-01.png", null, null)]
    public async Task RealPreparationFrameProducesFourAuditableAffixSlots(
        string fileName,
        string? expectedKnownSlot,
        string? expectedKnownId)
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        using var iconRecognizer = new OpenCvPhase2IconRecognizer();
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            iconRecognizer,
            Phase2IconTemplateCatalog.Load(dataDirectory),
            new EmptyOcr(),
            gameData,
            new EmptyOcr());
        var frame = CaptureFrameLoader.LoadFile(Path.Combine(
            RepositoryRoot,
            "tests",
            "CurrencyWarsAssistant.Tests",
            "Fixtures",
            "phase2-live-2026-07-29",
            fileName));

        var state = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            $"fixture:{fileName}",
            new RunSnapshot
            {
                RunId = $"affix-{fileName}",
                AsOf = frame.CapturedAt
            },
            CancellationToken.None);

        var slots = state.NamedContent
            .Where(item => item.Kind == Phase2NamedContentKind.NegativeAffix)
            .OrderBy(item => item.SlotKey, StringComparer.Ordinal)
            .ToArray();
        output.WriteLine(string.Join(
            Environment.NewLine,
            slots.Select(item =>
                $"{item.SlotKey}: {item.Status} id={item.ObjectId ?? "-"} " +
                $"candidates=[{string.Join(',', item.CandidateIds)}] " +
                $"confidence={item.Confidence:F3}")));
        output.WriteLine(string.Join(
            Environment.NewLine,
            state.PendingIcons
                .Where(item => item.Category == PendingIconCategory.NegativeAffix)
                .Select(item =>
                    $"pending {item.SlotKey}: template={item.TemplateId ?? "-"} " +
                    $"confidence={item.Confidence:F3} status={item.Status}")));

        Assert.Equal(4, slots.Length);
        Assert.All(slots, item => Assert.NotNull(item.Evidence));
        Assert.All(
            slots.Where(item => item.Status != ObservationStatus.Known),
            item => Assert.Contains(
                state.PendingIcons,
                pending =>
                    pending.Category == PendingIconCategory.NegativeAffix &&
                    pending.SlotKey == item.SlotKey &&
                    pending.Region == item.Region &&
                    pending.Evidence == item.Evidence));
        if (expectedKnownSlot is not null)
        {
            var known = Assert.Single(slots, item =>
                item.SlotKey == expectedKnownSlot);
            Assert.Equal(ObservationStatus.Known, known.Status);
            Assert.Equal(expectedKnownId, known.ObjectId);
            Assert.DoesNotContain(
                state.PendingIcons,
                pending =>
                    pending.Category == PendingIconCategory.NegativeAffix &&
                    pending.SlotKey == expectedKnownSlot);
        }

        Assert.Equal(
            slots.All(item =>
                item.Status == ObservationStatus.Known &&
                item.ObjectId is not null),
            state.NegativeAffixIds.Status == ObservationStatus.Known);
    }

    [Fact]
    public async Task LaterPartialFrameRetainsPreviouslyKnownAffixSlots()
    {
        var dataDirectory = Path.Combine(RepositoryRoot, "data", "4.4");
        var gameData = GameDataCatalogLoader.Load(dataDirectory);
        var firstId = gameData.EnemyAffixes[0].Id;
        var secondId = gameData.EnemyAffixes[1].Id;
        var analyzer = new Phase2OperationalScreenshotAnalyzer(
            new EmptyCharacterRecognizer(),
            [],
            new StagedAffixRecognizer(firstId, secondId),
            [],
            new EmptyOcr(),
            gameData,
            new EmptyOcr());
        var frame = EmptyFrame();
        var snapshot = new RunSnapshot
        {
            RunId = "partial-affix-slots",
            AsOf = frame.CapturedAt
        };

        var first = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:partial-affix-first",
            snapshot,
            CancellationToken.None);
        var second = await analyzer.AnalyzeAsync(
            frame,
            "preparation_generic",
            "fixture:partial-affix-second",
            snapshot,
            CancellationToken.None);

        Assert.Equal(ObservationStatus.Unknown, first.NegativeAffixIds.Status);
        Assert.Equal([firstId], first.NegativeAffixIds.Value);
        Assert.Equal(ObservationStatus.Unknown, second.NegativeAffixIds.Status);
        Assert.Equal([firstId, secondId], second.NegativeAffixIds.Value);
        var affixSlots = second.NamedContent
            .Where(item => item.Kind == Phase2NamedContentKind.NegativeAffix)
            .OrderBy(item => item.SlotKey, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(4, affixSlots.Length);
        Assert.Equal(firstId, affixSlots[0].ObjectId);
        Assert.Equal(secondId, affixSlots[1].ObjectId);
        Assert.DoesNotContain(
            second.PendingIcons,
            item =>
                item.Category == PendingIconCategory.NegativeAffix &&
                item.SlotKey is "NegativeAffix-1" or "NegativeAffix-2");
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));

    private static CaptureFrame EmptyFrame() => new(
        1920,
        1080,
        1920 * 4,
        new byte[1920 * 1080 * 4],
        new PixelRect(0, 0, 1920, 1080),
        DateTimeOffset.Parse("2026-08-01T09:00:00+08:00"));

    private sealed class EmptyOcr : IOfflineOcr
    {
        public bool IsAvailable => true;

        public ValueTask<OcrTextResult> RecognizeAsync(
            CaptureFrame frame,
            PixelRect region,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new OcrTextResult(string.Empty, []));
    }

    private sealed class EmptyCharacterRecognizer : ICharacterCardRecognizer
    {
        public IReadOnlyList<CharacterCardSlotRecognition> Recognize(
            CaptureFrame frame,
            IReadOnlyList<CharacterCardTemplateDefinition> templates,
            IReadOnlyList<PixelRect> referenceSlots) =>
            referenceSlots.Select((slot, index) =>
                    new CharacterCardSlotRecognition(
                        index,
                        slot,
                        CharacterCardSlotState.Empty,
                        null,
                        null,
                        1,
                        0,
                        0))
                .ToArray();
    }

    private sealed class StagedAffixRecognizer(
        string firstId,
        string secondId) : IPhase2IconRecognizer
    {
        private int _negativeAffixScans;

        public IReadOnlyList<Phase2IconRecognition> Recognize(
            CaptureFrame frame,
            string category,
            IReadOnlyList<NormalizedRect> slots,
            IReadOnlyList<Phase2IconTemplateDefinition> templates)
        {
            var scan = string.Equals(
                category,
                "negative-affix",
                StringComparison.Ordinal)
                ? Interlocked.Increment(ref _negativeAffixScans)
                : 0;
            return slots.Select((slot, index) =>
            {
                string? id = (scan, index) switch
                {
                    (1, 0) => firstId,
                    (2, 1) => secondId,
                    _ => null
                };
                return new Phase2IconRecognition(
                    index,
                    slot.ToPixels(frame.Width, frame.Height),
                    id,
                    id is null ? 0 : 0.95,
                    id is not null,
                    id is null ? [] : [id],
                    []);
            }).ToArray();
        }
    }
}
