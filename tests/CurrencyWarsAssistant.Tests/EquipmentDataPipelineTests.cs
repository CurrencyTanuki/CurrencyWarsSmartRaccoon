using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CurrencyWarsAssistant.Tests;

public sealed class EquipmentDataPipelineTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void SameRawInputProducesByteStableRuntimeAndReportAndPreservesUnknownFields()
    {
        using var sandbox = new TestDirectory();
        var rawDirectory = FindEmbeddedRawSnapshot();
        var protectedFiles = GetProtectedFileHashes();
        var firstRuntime = Path.Combine(sandbox.Path, "first-runtime");
        var firstReport = Path.Combine(sandbox.Path, "first-report");
        var secondRuntime = Path.Combine(sandbox.Path, "second-runtime");
        var secondReport = Path.Combine(sandbox.Path, "second-report");

        AssertPipelineSucceeded(RunPipeline(rawDirectory, firstRuntime, firstReport));
        AssertPipelineSucceeded(RunPipeline(rawDirectory, secondRuntime, secondReport));

        AssertDirectoriesEqual(firstRuntime, secondRuntime);
        AssertDirectoriesEqual(firstReport, secondReport);
        Assert.Equal(protectedFiles, GetProtectedFileHashes());

        using var runtime = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(firstRuntime, "equipment.json")));
        var root = runtime.RootElement;
        Assert.Equal("1.0.0", root.GetProperty("schema_version").GetString());
        Assert.Equal("4.4", root.GetProperty("game_version").GetString());
        Assert.Equal(157, root.GetProperty("records").GetArrayLength());

        var records = root.GetProperty("records").EnumerateArray().ToArray();
        Assert.DoesNotContain(records, item => item.GetProperty("name").GetString() == "财富");
        var wealthGem = Assert.Single(
            records.Where(item => item.GetProperty("name").GetString() == "财富宝钻"));
        Assert.Contains("团队规模上限+1", wealthGem.GetProperty("effect").GetString());

        var glove = Assert.Single(
            records.Where(item => item.GetProperty("id").GetString() ==
                                  "currency_wars_equipment_037"));
        var extensions = glove.GetProperty("source_extensions");
        Assert.True(extensions.TryGetProperty("mechanism_details", out _));
        Assert.True(extensions.TryGetProperty("effect_confidence", out _));

        using var report = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(firstReport, "conversion-report.json")));
        Assert.Equal("accepted", report.RootElement.GetProperty("status").GetString());
        Assert.Empty(report.RootElement.GetProperty("rejected_records").EnumerateArray());
        Assert.True(
            report.RootElement.GetProperty("unknown_fields")
                .GetProperty("record_fields")
                .TryGetProperty("mechanism_details", out _));
    }

    [Fact]
    public void IncompatibleSchemaVersionIsRejectedWithoutRuntimeOutput()
    {
        using var sandbox = new TestDirectory();
        var rawDirectory = CopyRawSnapshot(sandbox.Path);
        var packagePath = Path.Combine(rawDirectory, "package.json");
        var package = JsonNode.Parse(File.ReadAllText(packagePath))!.AsObject();
        package["schema_version"] = "2.0.0";
        WriteJson(packagePath, package);

        var runtime = Path.Combine(sandbox.Path, "runtime");
        var report = Path.Combine(sandbox.Path, "report");
        var result = RunPipeline(rawDirectory, runtime, report);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(runtime, "equipment.json")));
        AssertRejectedReportContains(report, "schema_version");
    }

    [Fact]
    public void UnknownEnumIsRejectedWithoutRuntimeOutput()
    {
        using var sandbox = new TestDirectory();
        var rawDirectory = CopyRawSnapshot(sandbox.Path);
        var recordsPath = Path.Combine(rawDirectory, "records.json");
        var raw = JsonNode.Parse(File.ReadAllText(recordsPath))!.AsObject();
        var records = raw["records"]!.AsArray();
        records[0]!["equipment_type"] = "not_a_real_equipment_type";
        WriteJson(recordsPath, raw);
        UpdatePackageHash(rawDirectory, "records", recordsPath);

        var runtime = Path.Combine(sandbox.Path, "runtime");
        var report = Path.Combine(sandbox.Path, "report");
        var result = RunPipeline(rawDirectory, runtime, report);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(runtime, "equipment.json")));
        AssertRejectedReportContains(report, "unknown enum value");
    }

    [Fact]
    public void DuplicateStableIdIsRejectedWithoutRuntimeOutput()
    {
        using var sandbox = new TestDirectory();
        var rawDirectory = CopyRawSnapshot(sandbox.Path);
        var recordsPath = Path.Combine(rawDirectory, "records.json");
        var raw = JsonNode.Parse(File.ReadAllText(recordsPath))!.AsObject();
        var records = raw["records"]!.AsArray();
        records[1]!["id"] = records[0]!["id"]!.GetValue<string>();
        WriteJson(recordsPath, raw);
        UpdatePackageHash(rawDirectory, "records", recordsPath);

        var runtime = Path.Combine(sandbox.Path, "runtime");
        var report = Path.Combine(sandbox.Path, "report");
        var result = RunPipeline(rawDirectory, runtime, report);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(runtime, "equipment.json")));
        AssertRejectedReportContains(report, "Duplicate equipment ID");
        AssertRejectedRecordsNotEmpty(report);
    }

    [Fact]
    public void MissingCrossFileReferenceIsRejectedWithoutRuntimeOutput()
    {
        using var sandbox = new TestDirectory();
        var rawDirectory = CopyRawSnapshot(sandbox.Path);
        var recordsPath = Path.Combine(rawDirectory, "records.json");
        var raw = JsonNode.Parse(File.ReadAllText(recordsPath))!.AsObject();
        var record = raw["records"]!.AsArray()
            .Select(item => item!.AsObject())
            .First(item => item["synthesis_components"] is JsonArray);
        record["synthesis_components"]!.AsArray()[0] = "不存在的装备引用";
        WriteJson(recordsPath, raw);
        UpdatePackageHash(rawDirectory, "records", recordsPath);

        var runtime = Path.Combine(sandbox.Path, "runtime");
        var report = Path.Combine(sandbox.Path, "report");
        var result = RunPipeline(rawDirectory, runtime, report);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(runtime, "equipment.json")));
        AssertRejectedReportContains(report, "unknown synthesis component");
        AssertRejectedRecordsNotEmpty(report);
    }

    private static string FindEmbeddedRawSnapshot()
    {
        var root = Path.Combine(RepositoryRoot, "data", "raw", "4.4", "equipment");
        return Assert.Single(
            Directory.GetDirectories(root)
                .Where(directory =>
                    File.Exists(Path.Combine(directory, "package.json")) &&
                    Directory.Exists(Path.Combine(
                        directory,
                        "assets",
                        "currency_wars_equipment_icons"))));
    }

    private static string CopyRawSnapshot(string destinationRoot)
    {
        var source = FindEmbeddedRawSnapshot();
        var destination = Path.Combine(destinationRoot, "raw");
        CopyDirectory(source, destination);
        return destination;
    }

    private static PipelineResult RunPipeline(
        string rawDirectory,
        string runtimeDirectory,
        string reportDirectory)
    {
        var script = Path.Combine(
            RepositoryRoot,
            "tools",
            "Invoke-EquipmentDataPipeline.ps1");
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-RawDirectory");
        startInfo.ArgumentList.Add(rawDirectory);
        startInfo.ArgumentList.Add("-RuntimeOutputDirectory");
        startInfo.ArgumentList.Add(runtimeDirectory);
        startInfo.ArgumentList.Add("-ReportDirectory");
        startInfo.ArgumentList.Add(reportDirectory);

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new PipelineResult(process.ExitCode, standardOutput, standardError);
    }

    private static void AssertPipelineSucceeded(PipelineResult result) =>
        Assert.True(
            result.ExitCode == 0,
            $"Pipeline failed. stdout:{Environment.NewLine}{result.StandardOutput}" +
            $"{Environment.NewLine}stderr:{Environment.NewLine}{result.StandardError}");

    private static void AssertRejectedReportContains(string reportDirectory, string text)
    {
        var path = Path.Combine(reportDirectory, "conversion-report.json");
        Assert.True(File.Exists(path));
        using var report = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("rejected", report.RootElement.GetProperty("status").GetString());
        Assert.False(report.RootElement.GetProperty("output_written").GetBoolean());
        Assert.Contains(
            report.RootElement.GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertRejectedRecordsNotEmpty(string reportDirectory)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(reportDirectory, "conversion-report.json")));
        Assert.NotEmpty(
            report.RootElement.GetProperty("rejected_records").EnumerateArray());
    }

    private static void UpdatePackageHash(
        string rawDirectory,
        string inputName,
        string inputPath)
    {
        var packagePath = Path.Combine(rawDirectory, "package.json");
        var package = JsonNode.Parse(File.ReadAllText(packagePath))!.AsObject();
        package["inputs"]![inputName]!["sha256"] = ComputeHash(inputPath);
        WriteJson(packagePath, package);
    }

    private static void WriteJson(string path, JsonNode value)
    {
        var json = value.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string ComputeHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static Dictionary<string, string> GetProtectedFileHashes()
    {
        var protectedPaths = Directory.GetFiles(
                Path.Combine(RepositoryRoot, "data", "4.4"),
                "*",
                SearchOption.AllDirectories)
            .Concat([
                Path.Combine(RepositoryRoot, "docs", "DATA_IMPORT_4.4.md"),
                Path.Combine(RepositoryRoot, "docs", "SCREEN_FLOW_1920x1080.md")
            ]);
        return protectedPaths.ToDictionary(
            path => Path.GetRelativePath(RepositoryRoot, path),
            ComputeHash,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertDirectoriesEqual(string expected, string actual)
    {
        var expectedFiles = Directory.GetFiles(expected, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(expected, path),
                ComputeHash,
                StringComparer.Ordinal);
        var actualFiles = Directory.GetFiles(actual, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(actual, path),
                ComputeHash,
                StringComparer.Ordinal);
        Assert.Equal(expectedFiles, actualFiles);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(
                     source,
                     "*",
                     SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(
                destination,
                Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, overwrite: false);
        }
    }

    private sealed record PipelineResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "currency-wars-equipment-pipeline-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
