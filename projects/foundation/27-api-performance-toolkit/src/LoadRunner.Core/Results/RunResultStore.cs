using System.Text.Json;
using System.Text.Json.Serialization;
using LoadRunner.Core.Scenarios;

namespace LoadRunner.Core.Results;

/// <summary>
/// Simple file-system backed store for run results. One JSON file per run keyed by run id.
/// Results are the input to the <c>loadrun compare</c> and <c>loadrun report</c> commands
/// as well as to any external CI that wants to archive them.
/// </summary>
public sealed class RunResultStore
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string RootDirectory { get; }

    public RunResultStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        Directory.CreateDirectory(rootDirectory);
    }

    public string Save(RunResult result)
    {
        var path = Path.Combine(RootDirectory, $"{result.RunId}.json");
        var json = JsonSerializer.Serialize(result, JsonOptions);
        File.WriteAllText(path, json);
        return path;
    }

    public RunResult Load(string runIdOrPath)
    {
        var path = runIdOrPath;
        if (!File.Exists(path))
        {
            var candidate = Path.Combine(RootDirectory, runIdOrPath);
            if (File.Exists(candidate)) path = candidate;
            else if (File.Exists(candidate + ".json")) path = candidate + ".json";
        }
        var json = File.ReadAllText(path);
        var result = JsonSerializer.Deserialize<RunResult>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Result at '{path}' deserialised to null");
        return result;
    }

    public IReadOnlyList<string> ListRuns() =>
        Directory.Exists(RootDirectory)
            ? Directory.GetFiles(RootDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
            : Array.Empty<string>();
}
