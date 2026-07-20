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

    var rawDir = Path.Combine(referenceDir, relativeDir, "Raw", fileName);
    var diffDir = Path.Combine(referenceDir, relativeDir, "Diff", fileName);

    Directory.CreateDirectory(rawDir);
    Directory.CreateDirectory(diffDir);

    var tunings = Gbx.ParseNode<CPlugVehiclePhyTunings>(filePath);

    var previousTuningFilePath = default(string);

    foreach (var (i, tuning) in tunings.Tuning?.Index() ?? [])
    {
        var tuningName = (tuning as CPlugVehicleCarPhyTuning)?.Name ?? tuning.Name;

        var tuningFilePath = Path.Combine(rawDir, $"{i:D3}_{tuningName}.txt");

        await using (var txtWriter = File.CreateText(tuningFilePath))
        {
            await txtWriter.WriteLineAsync($"Name: {tuningName}");
            await txtWriter.WriteLineAsync();

            var nodeType = tuning.GetType();

            foreach (var property in nodeType.GetProperties().OrderBy(x => x.Name))
            {
                if (property.Name is "Id" or "Chunks" or "GameVersion" or "Name")
                {
                    continue;
                }

                var value = property.GetValue(tuning);

                await txtWriter.WriteLineAsync(PropertyValueToString(property.Name, value));
            }

            foreach (var chunk in tuning.Chunks)
            {
                var chunkType = chunk.GetType();

                await txtWriter.WriteLineAsync();
                await txtWriter.WriteLineAsync($"0x{chunk.Id:X8}");

                foreach (var field in chunkType.GetFields().OrderBy(x => x.Name))
                {
                    var value = field.GetValue(chunk);

                    await txtWriter.WriteLineAsync(PropertyValueToString(field.Name, value));
                }
            }
        }

        if (previousTuningFilePath is not null)
        {
            var diffFilePath = Path.Combine(diffDir, $"{Path.GetFileNameWithoutExtension(tuningFilePath)}.diff");

            await WriteDiffFileAsync(previousTuningFilePath, tuningFilePath, diffFilePath);
        }

        previousTuningFilePath = tuningFilePath;
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

static string PropertyValueToString(string name, object? value)
{
    switch (value)
    {
        case CFuncKeysReal keys:
            value = KeysToString(keys);
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

static string ArrayToString(Array array)
{
    return $"[{string.Join(", ", array.Cast<object>().Select(x => x?.ToString() ?? "null"))}]";
}