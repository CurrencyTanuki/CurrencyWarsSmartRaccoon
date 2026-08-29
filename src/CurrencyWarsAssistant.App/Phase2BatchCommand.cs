using System.IO;

namespace CurrencyWarsAssistant.App;

public sealed record Phase2BatchCommand(
    string SourceDirectory,
    string OutputDirectory,
    bool ContinuousSequence,
    bool WriteAnnotations)
{
    public const string Switch = "--phase2-batch-test";

    public static Phase2BatchCommand? Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return null;
        }

        if (!string.Equals(arguments[0], Switch, StringComparison.Ordinal))
        {
            return null;
        }

        string? sourceDirectory = null;
        string? outputDirectory = null;
        var continuousSequence = false;
        var writeAnnotations = true;
        if (arguments.Count == 3)
        {
            sourceDirectory = arguments[1];
            outputDirectory = arguments[2];
        }
        else
        {
            for (var index = 1; index < arguments.Count; index++)
            {
                switch (arguments[index])
                {
                    case "--input":
                        sourceDirectory = arguments[++index];
                        break;
                    case "--output":
                        outputDirectory = arguments[++index];
                        break;
                    case "--continuous-sequence":
                        continuousSequence = true;
                        break;
                    case "--no-annotations":
                        writeAnnotations = false;
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(sourceDirectory) ||
            string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException(
                $"用法：{Switch} <测试图集目录> <独立报告目录>；或 " +
                $"{Switch} --input <测试图集目录> --output <独立报告目录>",
                nameof(arguments));
        }

        return new Phase2BatchCommand(
            Path.GetFullPath(sourceDirectory),
            Path.GetFullPath(outputDirectory),
            continuousSequence,
            writeAnnotations);
    }
}
