using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using CurrencyWarsAssistant.Automation;
using CurrencyWarsAssistant.Vision;

namespace CurrencyWarsAssistant.App.FrameSandbox;

public enum FrameSandboxMatchOutcome
{
    /// <summary>命中当前步尚未满足的期望（推进判据）。</summary>
    SatisfiesPending,
    /// <summary>命中脚本此前已满足过的期望（软件设计的重复点击：盲点连点/复点确认）。</summary>
    Repeat,
    /// <summary>脚本内没有任何期望与该操作相符=偏离最优路径（违规）。</summary>
    Unexpected
}

public sealed record FrameSandboxVerdict(
    string Status,
    string Reason,
    int StepsCompleted,
    int TotalSteps,
    int ViolationCount,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt)
{
    public const string StatusPass = "PASS";
    public const string StatusCompletedWithViolations = "COMPLETED_WITH_VIOLATIONS";
    public const string StatusFailed = "FAILED";
}

/// <summary>
/// 帧沙箱驱动器+裁判（docs/SANDBOX_FEASIBILITY_20260908.md §二）：
/// - AcquireFrame（FileSequenceGameCapture 每次捕获调用）供应当前步的帧；
///   引擎等待期重复捕获同一帧=自然通过等待。
/// - Record（RecordingInputController 每次模拟输入调用）做脚本匹配：
///   命中当前步全部期望→切下一帧；脚本走完=PASS；预期外操作/步超时=违规。
///   匹配对"期望操作"的大小写、顺序不敏感；与脚本内任何已满足期望相符的重复
///   操作（快速刷开局的盲点连点、复点确认）记为 Repeat 不算违规——它们是
///   最优路径自身的组成部分；只有与脚本内全部期望都不符的操作才是违规。
/// - 产物隔离在输出目录：sandbox-ops.jsonl / sandbox-violations.jsonl /
///   sandbox-verdict.txt（可行性案 §五.2 隔离要求）。
/// 线程安全：捕获（识别管线线程）、操作（执行器线程）、DECIDE 录制并发进入，
/// 全部状态经 _gate 保护。
/// </summary>
public sealed class FrameSandboxPlayer : ISandboxOperationSink, IDisposable
{
    /// <summary>
    /// 单条期望允许的重复操作上限（快速刷开局盲点连点 4s×50ms≈80 次 +
    /// 敌概盲点 3s≈60 次都远低于此；超过=软件陷入重复循环，如实记违规）。
    /// 对抗审查 P2-3：无上限的重复豁免会把弃局链的循环 Esc 吞成合法重复。
    /// </summary>
    public const int MaxRepeatsPerExpectation = 200;

    private readonly FrameSandboxScript _script;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly CaptureFrame?[] _frameCache;
    private readonly DateTimeOffset _startedAt;

    private int _stepIndex;
    private bool[] _matched;
    private bool _stepClockStarted;
    private DateTimeOffset _stepStartedAt;
    private FrameSandboxVerdict? _verdict;
    private readonly List<string> _violationLines = [];
    private readonly Dictionary<(int Step, int Expect), int> _repeatCounts = [];
    private bool _disposed;

    private TextWriter? _opsWriter;
    private TextWriter? _violationsWriter;
    private readonly string _opsPath;
    private readonly string _violationsPath;
    private readonly string _verdictPath;

    public FrameSandboxPlayer(
        FrameSandboxScript script,
        string outputDirectory,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _script = script;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startedAt = Now;
        _frameCache = new CaptureFrame?[script.Steps.Count];
        _matched = new bool[script.Steps[0].Expect.Count];
        Directory.CreateDirectory(outputDirectory);
        OutputDirectory = outputDirectory;
        _opsPath = Path.Combine(outputDirectory, "sandbox-ops.jsonl");
        _violationsPath = Path.Combine(
            outputDirectory,
            "sandbox-violations.jsonl");
        _verdictPath = Path.Combine(outputDirectory, "sandbox-verdict.txt");
        AppendOpsLine(new Dictionary<string, object?>
        {
            ["event"] = "sandbox-start",
            ["script"] = script.Name,
            ["steps"] = script.Steps.Count,
        });
    }

    public string OutputDirectory { get; }

    /// <summary>当前步索引（快照，仅供状态展示；判定一律走加锁路径）。</summary>
    public (int StepIndex, int TotalSteps, int ViolationCount) Status
    {
        get
        {
            lock (_gate)
            {
                return (_stepIndex + 1, _script.Steps.Count, _violationLines.Count);
            }
        }
    }

    /// <summary>终局判定；null=脚本仍在运行。</summary>
    public FrameSandboxVerdict? FinalVerdict
    {
        get
        {
            lock (_gate)
            {
                return _verdict;
            }
        }
    }

    /// <summary>识别管线每次捕获的入口：供应当前帧并做步超时判定。</summary>
    public CaptureFrame AcquireFrame()
    {
        lock (_gate)
        {
            TouchStepClock();
            CheckStepTimeout();
            var index = Math.Clamp(_stepIndex, 0, _script.Steps.Count - 1);
            _frameCache[index] ??= CaptureFrameLoader.LoadFile(
                _script.Steps[index].ImageFullPath);
            return _frameCache[index]!;
        }
    }

    /// <summary>RecordingInputController 每次模拟输入的入口：记录并判定。</summary>
    public void Record(SandboxOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            var judged = !_disposed && _verdict is null;
            FrameSandboxMatchOutcome outcome = FrameSandboxMatchOutcome.Unexpected;
            (int Step, int Index, FrameSandboxExpectation Expectation)? matched = null;
            if (judged)
            {
                TouchStepClock();
                (outcome, matched) = Match(operation);
                if (outcome == FrameSandboxMatchOutcome.SatisfiesPending)
                {
                    _matched[matched!.Value.Index] = true;
                }
            }

            AppendOpsLine(new Dictionary<string, object?>
            {
                ["event"] = "op",
                ["seq"] = operation.Sequence,
                ["at"] = operation.At,
                ["kind"] = operation.Kind,
                ["x"] = operation.X,
                ["y"] = operation.Y,
                ["toX"] = operation.ToX,
                ["toY"] = operation.ToY,
                ["key"] = operation.Key,
                ["modifier"] = operation.Modifier,
                ["step"] = judged ? _stepIndex + 1 : null,
                ["outcome"] = judged
                    ? outcome.ToString()
                    : "post-verdict",
                ["expectNote"] = matched?.Expectation.Note,
                ["message"] = operation.Message,
            });
            if (!judged)
            {
                return;
            }

            if (outcome == FrameSandboxMatchOutcome.Unexpected)
            {
                AddViolation(
                    "unexpected-op",
                    $"脚本外操作：{Describe(operation)}（当前第 {_stepIndex + 1} 步）");
            }
            else if (outcome == FrameSandboxMatchOutcome.Repeat)
            {
                var key = (matched!.Value.Step, matched.Value.Index);
                var count = _repeatCounts.TryGetValue(key, out var existing)
                    ? existing + 1
                    : 1;
                _repeatCounts[key] = count;
                if (count == MaxRepeatsPerExpectation + 1)
                {
                    // 只在首次破线时记违规，防止死循环刷屏；后续重复仍逐条留痕 ops。
                    AddViolation(
                        "repeat-cap-exceeded",
                        $"第 {key.Step + 1} 步期望「{DescribeExpectation(matched.Value.Expectation)}」" +
                        $"重复超过 {MaxRepeatsPerExpectation} 次——软件疑似陷入重复循环" +
                        $"（弃局链/重试环吞不掉真实偏离）。");
                }
            }

            if (outcome == FrameSandboxMatchOutcome.SatisfiesPending &&
                _matched.All(flag => flag))
            {
                CompleteStep();
            }
            else
            {
                CheckStepTimeout();
            }
        }
    }

    private (
        FrameSandboxMatchOutcome Outcome,
        (int Step, int Index, FrameSandboxExpectation Expectation)? Matched) Match(
        SandboxOperationRecord operation)
    {
        var step = _script.Steps[_stepIndex];
        for (var index = 0; index < step.Expect.Count; index++)
        {
            if (!_matched[index] &&
                Matches(step.Expect[index], operation))
            {
                return (FrameSandboxMatchOutcome.SatisfiesPending,
                    (_stepIndex, index, step.Expect[index]));
            }
        }

        // 重复容差：与脚本 0..当前步 任何"已满足/既往"期望相符的操作不算违规
        // （快速刷开局盲点连点/复点确认会跨帧重复已满足过的操作），但有上限——
        // 见 MaxRepeatsPerExpectation。
        for (var earlier = 0; earlier <= _stepIndex; earlier++)
        {
            var candidates = earlier == _stepIndex
                ? step.Expect
                : _script.Steps[earlier].Expect;
            for (var index = 0; index < candidates.Count; index++)
            {
                if (Matches(candidates[index], operation))
                {
                    return (FrameSandboxMatchOutcome.Repeat,
                        (earlier, index, candidates[index]));
                }
            }
        }

        return (FrameSandboxMatchOutcome.Unexpected, null);
    }

    private static bool Matches(
        FrameSandboxExpectation expectation,
        SandboxOperationRecord operation)
    {
        if (!string.Equals(expectation.Op, operation.Kind, StringComparison.Ordinal))
        {
            return false;
        }

        if (expectation.Tolerance < 0)
        {
            return false;
        }

        if (expectation.X is { } x && expectation.Y is { } y)
        {
            if (operation.X is not { } opX || operation.Y is not { } opY ||
                Math.Abs(opX - x) > expectation.Tolerance ||
                Math.Abs(opY - y) > expectation.Tolerance)
            {
                return false;
            }
        }

        if (expectation.ToX is { } toX && expectation.ToY is { } toY)
        {
            if (operation.ToX is not { } opToX || operation.ToY is not { } opToY ||
                Math.Abs(opToX - toX) > expectation.Tolerance ||
                Math.Abs(opToY - toY) > expectation.Tolerance)
            {
                return false;
            }
        }

        if (expectation.Key is { } key &&
            !string.Equals(key, operation.Key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (expectation.Modifier is { } modifier &&
            !string.Equals(
                modifier,
                operation.Modifier,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void CompleteStep()
    {
        if (_stepIndex >= _script.Steps.Count - 1)
        {
            Finalize(
                _violationLines.Count == 0
                    ? FrameSandboxVerdict.StatusPass
                    : FrameSandboxVerdict.StatusCompletedWithViolations,
                _violationLines.Count == 0
                    ? "脚本全部步骤按期望走完。"
                    : $"脚本走完，但累计 {_violationLines.Count} 条违规。");
            return;
        }

        _stepIndex++;
        _matched = new bool[_script.Steps[_stepIndex].Expect.Count];
        _stepClockStarted = false;
        if (_script.Steps[_stepIndex].Expect.Count == 0)
        {
            // 终局帧（空 expect）到达即视为脚本走完：后续操作不再判定。
            Finalize(
                _violationLines.Count == 0
                    ? FrameSandboxVerdict.StatusPass
                    : FrameSandboxVerdict.StatusCompletedWithViolations,
                _violationLines.Count == 0
                    ? $"已到达终局帧 {_script.Steps[_stepIndex].Image}。"
                    : $"已到达终局帧，但累计 {_violationLines.Count} 条违规。");
        }
    }

    private void TouchStepClock()
    {
        if (!_stepClockStarted)
        {
            _stepClockStarted = true;
            _stepStartedAt = Now;
        }
    }

    private void CheckStepTimeout()
    {
        if (_verdict is not null || !_stepClockStarted)
        {
            return;
        }

        var step = _script.Steps[_stepIndex];
        var elapsed = Now - _stepStartedAt;
        if (elapsed <= TimeSpan.FromSeconds(step.MaxWaitSeconds))
        {
            return;
        }

        AddViolation(
            "step-timeout",
            $"第 {_stepIndex + 1} 步超时（{elapsed.TotalSeconds:F0}s > " +
            $"{step.MaxWaitSeconds}s）：软件未按期望操作该帧 {step.Image}。剩余未满足期望：" +
            string.Join("；", step.Expect
                .Where((_, index) => !_matched[index])
                .Select(expectation => DescribeExpectation(expectation))));
        Finalize(
            FrameSandboxVerdict.StatusFailed,
            $"第 {_stepIndex + 1} 步超时未满足，判定失败。");
    }

    private void AddViolation(string type, string detail)
    {
        _violationLines.Add($"{type}: {detail}");
        AppendViolationsLine(new Dictionary<string, object?>
        {
            ["at"] = Now,
            ["step"] = _stepIndex + 1,
            ["type"] = type,
            ["detail"] = detail,
        });
    }

    private void Finalize(string status, string reason)
    {
        if (_verdict is not null)
        {
            return;
        }

        _verdict = new FrameSandboxVerdict(
            status,
            reason,
            _stepIndex >= _script.Steps.Count - 1
                ? _script.Steps.Count
                : _stepIndex,
            _script.Steps.Count,
            _violationLines.Count,
            _startedAt,
            Now);
        File.WriteAllLines(
            _verdictPath,
            new[]
            {
                $"verdict={status}",
                $"reason={reason}",
                $"stepsCompleted={_verdict.StepsCompleted}/{_verdict.TotalSteps}",
                $"violations={_verdict.ViolationCount}",
                $"startedAt={_verdict.StartedAt:O}",
                $"endedAt={_verdict.EndedAt:O}",
                $"script={_script.Name}",
            });
        AppendOpsLine(new Dictionary<string, object?>
        {
            ["event"] = "verdict",
            ["verdict"] = status,
            ["reason"] = reason,
            ["violations"] = _verdict.ViolationCount,
        });
    }

    private DateTimeOffset Now => _timeProvider.GetLocalNow();

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _opsWriter?.Dispose();
            _opsWriter = null;
            _violationsWriter?.Dispose();
            _violationsWriter = null;
        }
    }

    private static string Describe(SandboxOperationRecord operation)
    {
        var point = operation.X is { } x && operation.Y is { } y
            ? $"@({x},{y})"
            : string.Empty;
        var target = operation.ToX is { } toX && operation.ToY is { } toY
            ? $"→({toX},{toY})"
            : string.Empty;
        return $"{operation.Kind}{point}{target} {operation.Message}";
    }

    private static string DescribeExpectation(FrameSandboxExpectation expectation)
    {
        var point = expectation.X is { } x && expectation.Y is { } y
            ? $" near({x},{y})±{expectation.Tolerance}"
            : string.Empty;
        var target = expectation.ToX is { } toX && expectation.ToY is { } toY
            ? $" to({toX},{toY})"
            : string.Empty;
        var key = expectation.Key is { } k ? $" key={k}" : string.Empty;
        var modifier = expectation.Modifier is { } m ? $" modifier={m}" : string.Empty;
        return $"{expectation.Op}{point}{target}{key}{modifier}";
    }

    private void AppendOpsLine(IReadOnlyDictionary<string, object?> payload)
    {
        if (_disposed)
        {
            return; // Dispose 后不再重建文件（P3-2：防 FileMode.Create 截断已写内容）。
        }

        if (_opsWriter is null)
        {
            // FileShare.ReadWrite：允许值守/审计在沙箱运行中读取产物（读方也须带同款共享）。
            var stream = new FileStream(
                _opsPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite);
            _opsWriter = new StreamWriter(stream) { AutoFlush = true };
        }

        _opsWriter.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private void AppendViolationsLine(IReadOnlyDictionary<string, object?> payload)
    {
        if (_disposed)
        {
            return;
        }

        if (_violationsWriter is null)
        {
            var stream = new FileStream(
                _violationsPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite);
            _violationsWriter = new StreamWriter(stream) { AutoFlush = true };
        }

        _violationsWriter.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        // P3-1：中文按字面输出（值守可直读），不转义 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
