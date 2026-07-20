using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using GBX.NET;
using GBX.NET.Engines.Function;
using GBX.NET.Engines.Plug;
using System.Globalization;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var projectDir = Directory.GetParent(Environment.CurrentDirectory)?.Parent?.Parent?.FullName ?? throw new InvalidOperationException("Could not determine project directory.");
var referenceDir = Path.Combine(projectDir, "Reference");

var gameVehicleOrder = new Dictionary<string, (string DisplayName, string FileBaseName)[]>
{
    ["TM2020"] = [("StadiumCar", "TuningsSport"), ("SnowCar", "TuningsSnow"), ("RallyCar", "CarRally"), ("DesertCar", "DesertCar")],
    ["MP4"] = [("CanyonCar", "CanyonCar"), ("DesertCar", "DesertCar"), ("StadiumCar", "StadiumCar"), ("ValleyCar", "ValleyCar"), ("LagoonCar", "LagoonCar"), ("TrafficCar", "TrafficCar")],
    ["TMF"] = [("DesertCar", "American"), ("SnowCar", "Buggy"), ("RallyCar", "Rally"), ("IslandCar", "Sport"), ("BayCar", "BayCar"), ("CoastCar", "CoastCar"), ("StadiumCar", "StadiumCar")],
    ["TMN"] = [("StadiumCar", "StadiumCar")],
    ["TMSX"] = [("IslandCar", "Sport"), ("BayCar", "BayCar"), ("CoastCar", "CoastCar")],
    ["TM10"] = [("DesertCar", "American"), ("SnowCar", "Buggy"), ("RallyCar", "Rally")],
};

var filesByGame = Directory.EnumerateFiles("Tunings", "*.Gbx", SearchOption.AllDirectories)
    .GroupBy(filePath => Path.GetDirectoryName(Path.GetRelativePath("Tunings", filePath)) ?? throw new InvalidOperationException("Could not determine relative directory."));

foreach (var gameGroup in filesByGame)
{
    var relativeDir = gameGroup.Key;
    var gameReferenceDir = Path.Combine(referenceDir, relativeDir);
    var gameVehicles = new List<VehicleTableData>();
    var isMP = relativeDir is "MP4" or "TM2020";

    foreach (var filePath in gameGroup)
    {
        var fileName = Path.GetFileName(filePath);
        var fileBaseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(fileName));

        var tuningDir = Path.Combine(gameReferenceDir, fileName);
        var rawDir = Path.Combine(tuningDir, "Raw");
        var diffDir = Path.Combine(tuningDir, "Diff");
        var tablesDir = Path.Combine(tuningDir, "Tables");

        Directory.CreateDirectory(rawDir);
        Directory.CreateDirectory(diffDir);
        Directory.CreateDirectory(tablesDir);

        var tunings = Gbx.ParseNode<CPlugVehiclePhyTunings>(filePath);

        var previousTuningFilePath = default(string);
        var latestTablesFilePath = default(string);
        var latestProperties = new Dictionary<string, object?>();
        var latestChunks = new Dictionary<string, Dictionary<string, object?>>();

        foreach (var (i, tuning) in tunings.Tuning?.Index() ?? [])
        {
            var tuningName = (tuning as CPlugVehicleCarPhyTuning)?.Name ?? tuning.Name;

            var validTuningName = string.Join("_", tuningName.Split(Path.GetInvalidFileNameChars()));

            var tuningFilePath = Path.Combine(rawDir, $"{i:D3}_{validTuningName}.txt");
            var tablesFilePath = Path.Combine(tablesDir, $"{i:D3}_{validTuningName}.md");

            var properties = new Dictionary<string, object?>();
            var chunks = new Dictionary<string, Dictionary<string, object?>>();

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

                    properties[property.Name] = value;

                    await txtWriter.WriteLineAsync(ValueToString(property.Name, value, isMP));
                    await mdWriter.WriteLineAsync($"| {property.Name} | {ValueToMarkdownCell(property.Name, value, isMP)} |");
                }

                await mdWriter.WriteLineAsync("## Chunks");

                foreach (var chunk in tuning.Chunks)
                {
                    var chunkType = chunk.GetType();
                    var fields = chunkType.GetFields().OrderBy(x => x.Name).ToList();
                    var chunkId = $"0x{chunk.Id:X8}";
                    var chunkFields = new Dictionary<string, object?>();

                    await txtWriter.WriteLineAsync();
                    await txtWriter.WriteLineAsync(chunkId);

                    if (fields.Count > 0)
                    {
                        await mdWriter.WriteLineAsync();
                        await mdWriter.WriteLineAsync($"### {chunkId}");
                        await mdWriter.WriteLineAsync();
                        await mdWriter.WriteLineAsync("| Name | Value |");
                        await mdWriter.WriteLineAsync("| --- | --- |");
                    }

                    foreach (var field in fields)
                    {
                        var value = field.GetValue(chunk);

                        chunkFields[field.Name] = value;

                        await txtWriter.WriteLineAsync(ValueToString(field.Name, value, isMP));
                        await mdWriter.WriteLineAsync($"| {field.Name} | {ValueToMarkdownCell(field.Name, value, isMP)} |");
                    }

                    if (chunkFields.Count > 0)
                    {
                        chunks[chunkId] = chunkFields;
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
            latestProperties = properties;
            latestChunks = chunks;
        }

        if (latestTablesFilePath is not null)
        {
            var readmeFilePath = Path.Combine(tuningDir, "README.md");
            var latestContent = await File.ReadAllTextAsync(latestTablesFilePath);

            await using var readmeWriter = File.CreateText(readmeFilePath);
            await readmeWriter.WriteAsync(latestContent);

            gameVehicles.Add(new VehicleTableData(fileBaseName, fileName, latestProperties, latestChunks));
        }
    }

    var vehicleOrder = gameVehicleOrder.TryGetValue(relativeDir, out var configuredOrder) ? configuredOrder : [];

    await WriteGameReadmeAsync(gameReferenceDir, relativeDir, gameVehicles, vehicleOrder, isMP);
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

static string ValueToString(string name, object? value, bool isMP)
{
    switch (value)
    {
        case CFuncKeysReal or CPlugVehicleCarPhyTuning.Keys:
            var keyPoints = GetKeyPoints(value, isMP);
            value = keyPoints is null ? null : string.Join(" ", keyPoints.Points.Select(p => $"({p.X}, {p.Y})"));
            break;
        case Array array:
            value = ArrayToString(array);
            break;
    }

    return $"{name}: {value ?? "null"}";
}

static string ArrayToString(Array array)
{
    return $"[{string.Join(", ", array.Cast<object>().Select(x => x?.ToString() ?? "null"))}]";
}

static string ValueToMarkdownCell(string name, object? value, bool isMP)
{
    switch (value)
    {
        case CFuncKeysReal or CPlugVehicleCarPhyTuning.Keys:
            var keyPoints = GetKeyPoints(value, isMP);
            return keyPoints is null || keyPoints.Points.Length == 0
                ? "*(empty)*"
                : $"![{EscapeMarkdownCell(name)}]({KeysToChartUrl(keyPoints.Points, keyPoints.Interp)})";
        case Array array:
            return EscapeMarkdownCell(ArrayToString(array));
        default:
            return EscapeMarkdownCell(value?.ToString() ?? "null");
    }
}

static KeyPoints? GetKeyPoints(object? value, bool isMP)
{
    return value switch
    {
        CFuncKeysReal keys => new KeyPoints(ZipKeys(keys.Xs, keys.Ys), isMP ? keys.RealInterp : keys.RealInterp switch
        {
            CFuncKeysReal.ERealInterp.Linear => CFuncKeysReal.ERealInterp.None,
            CFuncKeysReal.ERealInterp.None => CFuncKeysReal.ERealInterp.Linear,
            _ => keys.RealInterp
        }),
        CPlugVehicleCarPhyTuning.Keys { U01: { } keys } => new KeyPoints(ZipKeys(keys.Xs, keys.Ys), keys.RealInterp),
        CPlugVehicleCarPhyTuning.Keys { U04: { } vecs } => new KeyPoints(vecs.Select(v => (v.X, v.Y)).ToArray(), CFuncKeysReal.ERealInterp.Linear),
        _ => null
    };
}

static (float X, float Y)[] ZipKeys(float[]? xs, float[]? ys)
{
    return (xs ?? []).Zip(ys ?? [], (x, y) => (x, y)).ToArray();
}

static string EscapeMarkdownCell(string text)
{
    return text.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");
}

static string KeysToChartUrl((float X, float Y)[] points, CFuncKeysReal.ERealInterp interp)
{
    var pointsJson = string.Join(",", points.Select(p => $"{{x:{p.X},y:{p.Y}}}"));
    var interpOptions = GetInterpOptions(interp);

    var config = $@"
{{
  type: 'line',
  data: {{
    datasets: [
      {{
        data: [{pointsJson}],
        fill: false,
        {interpOptions}
      }}
    ]
  }},
  options: {{
    plugins: {{
      legend: {{
        display: false
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

static string GetInterpOptions(CFuncKeysReal.ERealInterp interp)
{
    return interp switch
    {
        CFuncKeysReal.ERealInterp.None => "stepped:true",
        CFuncKeysReal.ERealInterp.Linear => "tension:0",
        CFuncKeysReal.ERealInterp.Hermite => "cubicInterpolationMode:'default',tension:0.4",
        CFuncKeysReal.ERealInterp.SmoothStep => "cubicInterpolationMode:'monotone',tension:0",
        _ => "tension:0"
    };
}

static async Task WriteGameReadmeAsync(string gameReferenceDir, string gameName, List<VehicleTableData> vehicles, (string DisplayName, string FileBaseName)[] vehicleOrder, bool isMP)
{
    if (vehicles.Count == 0)
    {
        return;
    }

    var orderedColumns = vehicleOrder
        .Select(o => (o.DisplayName, Vehicle: vehicles.Find(v => v.FileBaseName == o.FileBaseName)))
        .Where(c => c.Vehicle is not null)
        .Select(c => (c.DisplayName, Vehicle: c.Vehicle!))
        .ToList();

    var remainingColumns = vehicles
        .Where(v => orderedColumns.All(c => c.Vehicle.FileBaseName != v.FileBaseName))
        .OrderBy(v => v.FileBaseName)
        .Select(v => (DisplayName: v.FileBaseName, Vehicle: v));

    var columns = orderedColumns.Concat(remainingColumns).ToList();

    var readmeFilePath = Path.Combine(gameReferenceDir, "README.md");

    await using var writer = File.CreateText(readmeFilePath);

    await writer.WriteLineAsync("# Vehicle comparison");
    await writer.WriteLineAsync();
    await writer.WriteLineAsync("Comparison of the latest tuning revision of every vehicle.");
    await writer.WriteLineAsync();
    await writer.WriteLineAsync("## Properties");
    await writer.WriteLineAsync();
    await writer.WriteLineAsync($"| Property | {string.Join(" | ", columns.Select(c => $"[{c.DisplayName}]({c.Vehicle.FolderName})"))} |");
    await writer.WriteLineAsync($"| --- | {string.Join(" | ", columns.Select(_ => "---"))} |");

    var propertyNames = columns
        .SelectMany(c => c.Vehicle.Properties.Keys)
        .Distinct()
        .OrderBy(x => x);

    foreach (var propertyName in propertyNames)
    {
        var cells = columns.Select(c =>
        {
            c.Vehicle.Properties.TryGetValue(propertyName, out var value);
            return ValueToMarkdownCell(propertyName, value, isMP);
        });

        await writer.WriteLineAsync($"| {propertyName} | {string.Join(" | ", cells)} |");
    }

    var chunkIds = columns
        .SelectMany(c => c.Vehicle.Chunks.Keys)
        .Distinct()
        .OrderBy(x => x);

    await writer.WriteLineAsync();
    await writer.WriteLineAsync("## Chunks");

    foreach (var chunkId in chunkIds)
    {
        var fieldNames = columns
            .SelectMany(c => c.Vehicle.Chunks.TryGetValue(chunkId, out var chunkFields) ? (IEnumerable<string>)chunkFields.Keys : Array.Empty<string>())
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        if (fieldNames.Count == 0)
        {
            continue;
        }

        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"### {chunkId}");
        await writer.WriteLineAsync();
        await writer.WriteLineAsync($"| Field | {string.Join(" | ", columns.Select(c => $"[{c.DisplayName}]({c.Vehicle.FolderName})"))} |");
        await writer.WriteLineAsync($"| --- | {string.Join(" | ", columns.Select(_ => "---"))} |");

        foreach (var fieldName in fieldNames)
        {
            var cells = columns.Select(c =>
            {
                object? value = null;

                if (c.Vehicle.Chunks.TryGetValue(chunkId, out var chunkFields))
                {
                    chunkFields.TryGetValue(fieldName, out value);
                }

                return ValueToMarkdownCell(fieldName, value, isMP);
            });

            await writer.WriteLineAsync($"| {fieldName} | {string.Join(" | ", cells)} |");
        }
    }
}

record KeyPoints((float X, float Y)[] Points, CFuncKeysReal.ERealInterp Interp);
record VehicleTableData(string FileBaseName, string FolderName, Dictionary<string, object?> Properties, Dictionary<string, Dictionary<string, object?>> Chunks);