using System.IO;
using System.Text.Json;

namespace CurrencyWarsAssistant.App.FrameSandbox;

/// <summary>
/// 帧沙箱脚本的一条期望操作。坐标为 1920×1080 标准系客户区坐标。
/// Op：click / drag / presskey / clickwithmodifier。
/// click 类必须有 near；drag 可带 to（拖拽目标点）；presskey 必须有 key；
/// clickwithmodifier 必须有 modifier；tol 默认 30（可行性案 §二：标准几何无抖动）。
/// </summary>
public sealed record FrameSandboxExpectation(
    string Op,
    int? X,
    int? Y,
    int? ToX,
    int? ToY,
    string? Key,
    string? Modifier,
    int Tolerance,
    string? Note)
{
    public const string OpClick = "click";
    public const string OpDrag = "drag";
    public const string OpPressKey = "presskey";
    public const string OpClickWithModifier = "clickwithmodifier";

    public static readonly IReadOnlySet<string> KnownOps =
        new HashSet<string>(StringComparer.Ordinal)
        {
            OpClick,
            OpDrag,
            OpPressKey,
            OpClickWithModifier
        };

    public bool RequiresPoint =>
        Op is OpClick or OpDrag or OpClickWithModifier;
}

public sealed record FrameSandboxStep(
    string Image,
    string ImageFullPath,
    IReadOnlyList<FrameSandboxExpectation> Expect,
    int MaxWaitSeconds);

public sealed record FrameSandboxScript(
    string Name,
    string Directory,
    IReadOnlyList<FrameSandboxStep> Steps);

/// <summary>
/// 帧沙箱脚本加载器（JSON，schema 见 docs/SANDBOX_FEASIBILITY_20260908.md §二）。
/// 图片路径相对脚本文件目录解析；加载时即校验存在性与期望合法性，
/// 非法脚本拒绝启动沙箱（宁可不起，不可误判）。
/// </summary>
public static class FrameSandboxScriptLoader
{
    public const int DefaultTolerance = 30;
    public const int DefaultMaxWaitSeconds = 120;

    private static readonly IReadOnlySet<string> KnownKeys =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "escape",
            "leftalt",
            "v",
            "enter",
            "f"
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static FrameSandboxScript Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var json = File.ReadAllText(fullPath);
        var document = JsonSerializer.Deserialize<ScriptDocument>(json, JsonOptions) ??
                       throw new InvalidDataException($"帧沙箱脚本为空：{fullPath}");
        if (document.Steps is null || document.Steps.Count == 0)
        {
            throw new InvalidDataException($"帧沙箱脚本没有任何步骤：{fullPath}");
        }

        var scriptDirectory = Path.GetDirectoryName(fullPath)
                              ?? throw new InvalidDataException(
                                  $"帧沙箱脚本没有所在目录：{fullPath}");
        var defaultMaxWait = document.DefaultMaxWaitSeconds ??
                             DefaultMaxWaitSeconds;
        if (defaultMaxWait <= 0)
        {
            throw new InvalidDataException(
                $"defaultMaxWaitSeconds 必须为正数：{defaultMaxWait}");
        }

        var steps = new List<FrameSandboxStep>(document.Steps.Count);
        for (var index = 0; index < document.Steps.Count; index++)
        {
            var dto = document.Steps[index];
            if (string.IsNullOrWhiteSpace(dto.Image))
            {
                throw new InvalidDataException(
                    $"第 {index + 1} 步缺少 image 字段：{fullPath}");
            }

            var imageFullPath = Path.IsPathFullyQualified(dto.Image)
                ? Path.GetFullPath(dto.Image)
                : Path.GetFullPath(Path.Combine(scriptDirectory, dto.Image));
            if (!File.Exists(imageFullPath))
            {
                throw new InvalidDataException(
                    $"第 {index + 1} 步的帧图片不存在：{imageFullPath}");
            }

            var expects = new List<FrameSandboxExpectation>();
            if (dto.Expect is not null)
            {
                for (var expectIndex = 0; expectIndex < dto.Expect.Count; expectIndex++)
                {
                    expects.Add(ParseExpectation(
                        index,
                        expectIndex,
                        dto.Expect[expectIndex],
                        fullPath));
                }
            }

            if (expects.Count == 0 && index != document.Steps.Count - 1)
            {
                throw new InvalidDataException(
                    $"第 {index + 1} 步没有任何期望操作；空 expect 只允许出现在" +
                    $"最后一步（终局帧）：{fullPath}");
            }

            // P3-5（对抗审查）：单步终局帧脚本永远等不到"到达终局"的推进事件，
            // 只能等超时判 FAILED——直接拒绝加载，宁可不起不可误判。
            if (expects.Count == 0 && document.Steps.Count == 1)
            {
                throw new InvalidDataException(
                    "脚本只有一个空期望的终局步骤，无法判定到达；" +
                    "至少需要一个带 expect 的步骤：" + fullPath);
            }

            var maxWait = dto.MaxWaitSeconds ?? defaultMaxWait;
            if (maxWait <= 0)
            {
                throw new InvalidDataException(
                    $"第 {index + 1} 步 maxWaitSeconds 必须为正数：{maxWait}");
            }

            steps.Add(new FrameSandboxStep(
                dto.Image,
                imageFullPath,
                expects,
                maxWait));
        }

        return new FrameSandboxScript(
            string.IsNullOrWhiteSpace(document.Name)
                ? Path.GetFileNameWithoutExtension(fullPath)
                : document.Name!,
            scriptDirectory,
            steps);
    }

    private static FrameSandboxExpectation ParseExpectation(
        int stepIndex,
        int expectIndex,
        ExpectationDocument dto,
        string scriptPath)
    {
        var where = $"第 {stepIndex + 1} 步第 {expectIndex + 1} 条期望";
        var op = dto.Op?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(op) || !FrameSandboxExpectation.KnownOps.Contains(op))
        {
            throw new InvalidDataException(
                $"{where}的 op 非法：{dto.Op}（允许：" +
                string.Join('/', FrameSandboxExpectation.KnownOps) + $"）：{scriptPath}");
        }

        var (x, y) = ParsePoint(dto.Near, $"{where} 的 near", op, scriptPath);
        var (toX, toY) = ParsePoint(dto.To, $"{where} 的 to", op, scriptPath);
        string? key = null;
        string? modifier = null;
        if (op == FrameSandboxExpectation.OpPressKey)
        {
            key = dto.Key?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || !KnownKeys.Contains(key))
            {
                throw new InvalidDataException(
                    $"{where}的 key 非法：{dto.Key}（允许：" +
                    string.Join('/', KnownKeys) + $"）：{scriptPath}");
            }
        }
        else if (op == FrameSandboxExpectation.OpClickWithModifier)
        {
            modifier = dto.Modifier?.Trim().ToLowerInvariant() ?? dto.Key?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(modifier) || !KnownKeys.Contains(modifier))
            {
                throw new InvalidDataException(
                    $"{where}的 modifier 非法：{dto.Modifier}（允许：" +
                    string.Join('/', KnownKeys) + $"）：{scriptPath}");
            }
        }

        var tolerance = dto.Tol ?? dto.Tolerance ?? DefaultTolerance;
        if (tolerance < 0)
        {
            throw new InvalidDataException(
                $"{where}的 tol 不能为负：{tolerance}：{scriptPath}");
        }

        var requiresPoint = op
            is FrameSandboxExpectation.OpClick
            or FrameSandboxExpectation.OpDrag
            or FrameSandboxExpectation.OpClickWithModifier;
        if (requiresPoint && (x is null || y is null))
        {
            throw new InvalidDataException(
                $"{where}（{op}）缺少 near:[x,y] 坐标：{scriptPath}");
        }

        return new FrameSandboxExpectation(
            op,
            x,
            y,
            toX,
            toY,
            key,
            modifier,
            tolerance,
            dto.Note);
    }

    private static (int? X, int? Y) ParsePoint(
        int[]? point,
        string field,
        string op,
        string scriptPath)
    {
        if (point is null || point.Length == 0)
        {
            return (null, null);
        }

        if (point.Length != 2)
        {
            throw new InvalidDataException(
                $"{field} 必须是 [x,y] 两元素数组：{scriptPath}");
        }

        return (point[0], point[1]);
    }

    private sealed record ScriptDocument(
        string? Name,
        int? DefaultMaxWaitSeconds,
        IReadOnlyList<StepDocument>? Steps);

    private sealed record StepDocument(
        string? Image,
        int? MaxWaitSeconds,
        IReadOnlyList<ExpectationDocument>? Expect);

    private sealed record ExpectationDocument(
        string? Op,
        int[]? Near,
        int[]? To,
        string? Key,
        string? Modifier,
        int? Tol,
        int? Tolerance,
        string? Note);
}
