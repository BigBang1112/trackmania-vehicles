using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using GBX.NET;
using GBX.NET.Engines.Function;
using GBX.NET.Engines.Plug;
using GBX.NET.LZO;
using System.Globalization;

Gbx.LZO = new Lzo();

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var projectDir = Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName ?? throw new InvalidOperationException("Could not determine project directory.");
var referenceDir = Path.Combine(projectDir, "Reference");

foreach (var filePath in Directory.EnumerateFiles("Tunings", "*.Gbx", SearchOption.AllDirectories))
{
    var fileName = Path.GetFileName(filePath);
    var relativePath = Path.GetRelativePath("Tunings", filePath);
    var relativeDir = Path.GetDirectoryName(relativePath) ?? throw new InvalidOperationException("Could not determine relative directory.");

    var tuningDir = Path.Combine(referenceDir, relativeDir, fileName);
    var rawDir = Path.Combine(tuningDir, "Raw");
    var diffDir = Path.Combine(tuningDir, "Diff");
    var tablesDir = Path.Combine(tuningDir, "Tables");

    Directory.CreateDirectory(rawDir);
    Directory.CreateDirectory(diffDir);
    Directory.CreateDirectory(tablesDir);

    var tunings = Gbx.ParseNode<CPlugVehiclePhyTunings>(filePath);

    var previousTuningFilePath = default(string);
    var latestTablesFilePath = default(string);

    foreach (var (i, tuning) in tunings.Tuning?.Index() ?? [])
    {
        var tuningName = (tuning as CPlugVehicleCarPhyTuning)?.Name ?? tuning.Name;

        var validTuningName = string.Join("_", tuningName.Split(Path.GetInvalidFileNameChars()));

        var tuningFilePath = Path.Combine(rawDir, $"{i:D3}_{validTuningName}.txt");
        var tablesFilePath = Path.Combine(tablesDir, $"{i:D3}_{validTuningName}.md");

        await using (var txtWriter = File.CreateText(tuningFilePath))
        await using (var mdWriter = File.CreateText(tablesFilePath))
        {
            await txtWriter.WriteLineAsync($"Name: {tuningName}");
            await txtWriter.WriteLineAsync();

            await mdWriter.WriteLineAsync($"# {tuningName}");
            await mdWriter.WriteLineAsync();
            await mdWriter.WriteLineAsync("## Properties");
            await mdWriter.WriteLineAsync();
            await mdWriter.WriteLineAsync("| Name | Value |");
            await mdWriter.WriteLineAsync("| --- | --- |");

            var nodeType = tuning.GetType();

            foreach (var property in nodeType.GetProperties().OrderBy(x => x.Name))
            {
                if (property.Name is "Id" or "Chunks" or "GameVersion" or "Name")
                {
                    continue;
                }

                var value = property.GetValue(tuning);

                await txtWriter.WriteLineAsync(ValueToString(property.Name, value));
                await mdWriter.WriteLineAsync($"| {property.Name} | {ValueToMarkdownCell(property.Name, value)} |");
            }

            await mdWriter.WriteLineAsync("## Chunks");

            foreach (var chunk in tuning.Chunks)
            {
                var chunkType = chunk.GetType();
                var fields = chunkType.GetFields().OrderBy(x => x.Name).ToList();

                await txtWriter.WriteLineAsync();
                await txtWriter.WriteLineAsync($"0x{chunk.Id:X8}");

                if (fields.Count > 0)
                {
                    await mdWriter.WriteLineAsync();
                    await mdWriter.WriteLineAsync($"### 0x{chunk.Id:X8}");
                    await mdWriter.WriteLineAsync();
                    await mdWriter.WriteLineAsync("| Name | Value |");
                    await mdWriter.WriteLineAsync("| --- | --- |");
                }

                foreach (var field in fields)
                {
                    var value = field.GetValue(chunk);

                    await txtWriter.WriteLineAsync(ValueToString(field.Name, value));
                    await mdWriter.WriteLineAsync($"| {field.Name} | {ValueToMarkdownCell(field.Name, value)} |");
                }
            }
        }

        if (previousTuningFilePath is not null)
        {
            var diffFilePath = Path.Combine(diffDir, $"{Path.GetFileNameWithoutExtension(tuningFilePath)}.diff");

            await WriteDiffFileAsync(previousTuningFilePath, tuningFilePath, diffFilePath);
        }

        previousTuningFilePath = tuningFilePath;
        latestTablesFilePath = tablesFilePath;
    }

    if (latestTablesFilePath is not null)
    {
        var readmeFilePath = Path.Combine(tuningDir, "README.md");
        var latestContent = await File.ReadAllTextAsync(latestTablesFilePath);

        await using var readmeWriter = File.CreateText(readmeFilePath);
        await readmeWriter.WriteAsync(latestContent);
    }
}

static async Task WriteDiffFileAsync(string oldFilePath, string newFilePath, string diffFilePath)
{
    var oldText = await File.ReadAllTextAsync(oldFilePath);
    var newText = await File.ReadAllTextAsync(newFilePath);

    var diff = InlineDiffBuilder.Diff(oldText, newText);

    await using var diffWriter = File.CreateText(diffFilePath);

    if (!diff.Lines.Any(x => x.Type is ChangeType.Inserted or ChangeType.Deleted))
    {
        await diffWriter.WriteLineAsync("No differences");
        return;
    }

    foreach (var line in diff.Lines)
    {
        var prefix = line.Type switch
        {
            ChangeType.Inserted => "+ ",
            ChangeType.Deleted => "- ",
            ChangeType.Unchanged => "  ",
            _ => "? "
        };

        await diffWriter.WriteLineAsync(prefix + line.Text);
    }
}

static string ValueToString(string name, object? value)
{
    switch (value)
    {
        case CFuncKeysReal keys:
            value = KeysToString(keys);
            break;
        case CPlugVehicleCarPhyTuning.Keys tuningKeys:
            if (tuningKeys.U01 is not null)
            {
                value = KeysToString(tuningKeys.U01);
            }
            else if (tuningKeys.U04 is not null)
            {
                value = KeysVec2ToString(tuningKeys.U04);
            }
            break;
        case Array array:
            value = ArrayToString(array);
            break;
    }

    return $"{name}: {value ?? "null"}";
}

static string KeysToString(CFuncKeysReal keys)
{
    var xs = keys.Xs ?? [];
    var ys = keys.Ys ?? [];

    return string.Join(" ", xs.Zip(ys, (x, y) => $"({x}, {y})"));
}

static string KeysVec2ToString(Vec2[]? keys)
{
    if (keys is null)
    {
        return "null";
    }

    return string.Join(" ", keys.Select(k => $"({k.X}, {k.Y})"));
}

static string ArrayToString(Array array)
{
    return $"[{string.Join(", ", array.Cast<object>().Select(x => x?.ToString() ?? "null"))}]";
}

static string ValueToMarkdownCell(string name, object? value)
{
    switch (value)
    {
        case CFuncKeysReal keys:
            var xs = keys.Xs ?? [];
            var ys = keys.Ys ?? [];

            if (xs.Length == 0)
            {
                return "*(empty)*";
            }

            return $"![{EscapeMarkdownCell(name)}]({KeysToChartUrl(xs, ys)})";
        case Array array:
            return EscapeMarkdownCell(ArrayToString(array));
        default:
            return EscapeMarkdownCell(value?.ToString() ?? "null");
    }
}

static string EscapeMarkdownCell(string text)
{
    return text.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");
}

static string KeysToChartUrl(float[] xs, float[] ys)
{
    var points = string.Join(",", xs.Zip(ys, (x, y) => $"{{x:{x},y:{y}}}"));

    var config = $@"
{{
  type: 'line',
  data: {{
    datasets: [
      {{
        data: [{points}],
        fill: false
      }}
    ]
  }},
  options: {{
    plugins: {{
      legend: {{
        display: false
      }}
    }},
    elements: {{
      line: {{
        cubicInterpolationMode: 'default'
      }}
    }},
    scales: {{
      x: {{
        type: 'linear'
      }}
    }}
  }}
}}";

    config = string.Concat(config.Where(c => !char.IsWhiteSpace(c)));

    return $"https://quickchart.io/chart?version=4&devicePixelRatio=1&width=300&height=150&c={Uri.EscapeDataString(config)}";
}