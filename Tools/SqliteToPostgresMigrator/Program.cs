using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;
using Npgsql;

var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var sqlitePath = args.Length > 0
    ? args[0]
    : @"C:\Users\jack\Documents\Playground\HID\ScrcpyKeyboardClient\AnnotationTemplates\annotations.sqlite3";
var connectionString = args.Length > 1
    ? args[1]
    : "Host=127.0.0.1;Port=5432;Database=whatjolo;Username=postgres;Password=postgres";
var schemaPath = args.Length > 2
    ? args[2]
    : Path.Combine(repoRoot, "pg_schema.sql");

if (!File.Exists(sqlitePath))
{
    Console.Error.WriteLine($"SQLite non trovato: {sqlitePath}");
    return 1;
}

if (!File.Exists(schemaPath))
{
    Console.Error.WriteLine($"Schema PostgreSQL non trovato: {schemaPath}");
    return 1;
}

await using var sqlite = new SqliteConnection($"Data Source={sqlitePath};Mode=ReadOnly");
await sqlite.OpenAsync();

await using var postgres = new NpgsqlConnection(connectionString);
await postgres.OpenAsync();

Console.WriteLine($"SQLite: {sqlitePath}");
Console.WriteLine("PostgreSQL: connessione aperta.");

await ApplySchemaAsync(postgres, schemaPath);
await using var tx = await postgres.BeginTransactionAsync();

try
{
    var imported = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    imported["cropasset"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "CropAsset",
        "cropasset",
        ["id", "sourceimagepath", "cropimagepath", "crophash", "x", "y", "width", "height", "createdatutc", "updatedatutc"]);

    imported["projectcroplink"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "ProjectCropLink",
        "projectcroplink",
        ["id", "projectname", "labelname", "cropassetid", "createdatutc", "updatedatutc", "isvariation"]);

    imported["contacts"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "Contacts",
        "contacts",
        ["id", "phonenumber", "firstname", "lastname", "createdatutc", "updatedatutc", "phonenumbernormalized", "chat", "test", "sent", "exclude"]);

    imported["incomingmessages"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "IncomingMessages",
        "incomingmessages",
        ["id", "phonenumber", "messagetimestamputc", "messagetext", "createdatutc", "messagetype", "whatsappmessageid", "messageack", "isfromme"]);

    imported["messagenotifications"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "MessageNotifications",
        "messagenotifications",
        ["id", "incomingmessageid", "phonenumber", "createdatutc", "processed"]);

    imported["projectactiveclass"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "ProjectActiveClass",
        "projectactiveclass",
        ["id", "projectname", "classname", "createdatutc", "updatedatutc"]);

    imported["projectinfo"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "ProjectInfo",
        "projectinfo",
        ["id", "projectname", "projectrootpath", "machinename", "createdatutc", "updatedatutc"]);

    imported["projectimageblob"] = await CopyTableAsync(
        sqlite,
        postgres,
        tx,
        "ProjectImageBlob",
        "projectimageblob",
        ["id", "projectname", "imagepath", "imagekind", "contenthash", "bytelength", "compressedbytes", "createdatutc", "updatedatutc"]);

    await ResetSequencesAsync(postgres, tx, imported.Keys);
    await tx.CommitAsync();

    Console.WriteLine("Migrazione completata.");
    foreach (var kvp in imported)
    {
        Console.WriteLine($"{kvp.Key}: {kvp.Value}");
    }

    return 0;
}
catch (Exception ex)
{
    await tx.RollbackAsync();
    Console.Error.WriteLine("Migrazione fallita.");
    Console.Error.WriteLine(ex);
    return 1;
}

static async Task ApplySchemaAsync(NpgsqlConnection postgres, string schemaPath)
{
    var sql = await File.ReadAllTextAsync(schemaPath, Encoding.UTF8);
    await using var command = new NpgsqlCommand(sql, postgres);
    await command.ExecuteNonQueryAsync();
}

static async Task<int> CopyTableAsync(
    SqliteConnection sqlite,
    NpgsqlConnection postgres,
    NpgsqlTransaction tx,
    string sourceTable,
    string targetTable,
    IReadOnlyList<string> targetColumns)
{
    var sourceColumns = targetColumns
        .Select(ToSourceColumnName)
        .ToArray();

    await using var select = sqlite.CreateCommand();
    select.CommandText = $"SELECT {string.Join(", ", sourceColumns.Select(c => $"[{c}]"))} FROM [{sourceTable}]";

    await using var reader = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
    var parameterNames = Enumerable.Range(0, targetColumns.Count).Select(i => $"@p{i}").ToArray();
    var insertSql =
        $"INSERT INTO {targetTable} ({string.Join(", ", targetColumns)}) VALUES ({string.Join(", ", parameterNames)})";

    var count = 0;
    while (await reader.ReadAsync())
    {
        await using var insert = new NpgsqlCommand(insertSql, postgres, tx);
        for (var i = 0; i < targetColumns.Count; i++)
        {
            insert.Parameters.AddWithValue(parameterNames[i], NormalizeValue(reader.GetValue(i)));
        }

        await insert.ExecuteNonQueryAsync();
        count++;
    }

    return count;
}

static object NormalizeValue(object value)
{
    if (value is DBNull)
    {
        return DBNull.Value;
    }

    if (value is byte[] bytes)
    {
        return bytes;
    }

    if (value is string text)
    {
        return SanitizeText(text);
    }

    return value;
}

static string SanitizeText(string input)
{
    if (string.IsNullOrEmpty(input))
    {
        return input;
    }

    var builder = new StringBuilder(input.Length);
    foreach (var ch in input)
    {
        if (ch == '\0')
        {
            continue;
        }

        if (char.IsSurrogate(ch))
        {
            builder.Append('\uFFFD');
            continue;
        }

        builder.Append(ch);
    }

    return builder.ToString();
}

static string ToSourceColumnName(string lowerColumnName)
{
    return lowerColumnName switch
    {
        "id" => "Id",
        "sourceimagepath" => "SourceImagePath",
        "cropimagepath" => "CropImagePath",
        "crophash" => "CropHash",
        "x" => "X",
        "y" => "Y",
        "width" => "Width",
        "height" => "Height",
        "createdatutc" => "CreatedAtUtc",
        "updatedatutc" => "UpdatedAtUtc",
        "projectname" => "ProjectName",
        "labelname" => "LabelName",
        "cropassetid" => "CropAssetId",
        "isvariation" => "IsVariation",
        "phonenumber" => "PhoneNumber",
        "firstname" => "FirstName",
        "lastname" => "LastName",
        "phonenumbernormalized" => "PhoneNumberNormalized",
        "chat" => "Chat",
        "test" => "Test",
        "sent" => "Sent",
        "exclude" => "Exclude",
        "messagetimestamputc" => "MessageTimestampUtc",
        "messagetext" => "MessageText",
        "messagetype" => "MessageType",
        "whatsappmessageid" => "WhatsAppMessageId",
        "messageack" => "MessageAck",
        "isfromme" => "IsFromMe",
        "incomingmessageid" => "IncomingMessageId",
        "processed" => "Processed",
        "classname" => "ClassName",
        "projectrootpath" => "ProjectRootPath",
        "machinename" => "MachineName",
        "imagepath" => "ImagePath",
        "imagekind" => "ImageKind",
        "contenthash" => "ContentHash",
        "bytelength" => "ByteLength",
        "compressedbytes" => "CompressedBytes",
        _ => throw new InvalidOperationException($"Mapping colonna non gestito: {lowerColumnName}")
    };
}

static async Task ResetSequencesAsync(
    NpgsqlConnection postgres,
    NpgsqlTransaction tx,
    IEnumerable<string> tables)
{
    foreach (var table in tables)
    {
        var sql = $"""
                   SELECT setval(
                       pg_get_serial_sequence('{table}', 'id'),
                       COALESCE((SELECT MAX(id) FROM {table}), 1),
                       (SELECT COUNT(*) > 0 FROM {table})
                   );
                   """;

        await using var command = new NpgsqlCommand(sql, postgres, tx);
        await command.ExecuteNonQueryAsync();
    }
}
