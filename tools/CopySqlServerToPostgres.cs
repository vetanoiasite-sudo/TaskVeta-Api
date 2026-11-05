// One-time data migration: copies every table from the old SQL Server database into the PostgreSQL database.
//
// Usage (from backend/):
//   1. Create the empty schema:  dotnet ef database update -p src/CompanyManagement.Infrastructure -s src/CompanyManagement.API
//   2. Copy the data:            dotnet run tools/CopySqlServerToPostgres.cs -- "<sql server connection string>"
//
// The PostgreSQL connection string is read from the API's user-secrets (ConnectionStrings:DefaultConnection),
// or from the CM_PG_CONNECTION environment variable, so no password is typed on the command line.
// Target tables are emptied first, so the tool can be re-run safely.
#:package Microsoft.Data.SqlClient@6.1.2
#:package Npgsql@10.0.0
#:package System.Security.Cryptography.Pkcs@10.0.14

using System.Text.Json;
using Microsoft.Data.SqlClient;
using Npgsql;

var source = args.Length > 0 ? args[0] : "Server=.;Database=CompanyManagement;Trusted_Connection=True;TrustServerCertificate=True";
var target = Environment.GetEnvironmentVariable("CM_PG_CONNECTION") ?? ReadUserSecret("9a3642ae-1ab0-49ad-be7f-faf343aea45d", "ConnectionStrings:DefaultConnection")
             ?? throw new InvalidOperationException("PostgreSQL connection string not found in user-secrets or CM_PG_CONNECTION.");

await using var sql = new SqlConnection(source);
await sql.OpenAsync();
await using var pg = new NpgsqlConnection(target);
await pg.OpenAsync();

// Tables + columns as they exist in PostgreSQL (created by the EF migration). System columns such as xmin are not listed.
var columns = new Dictionary<string, List<(string Name, string Type)>>();
await using (var cmd = new NpgsqlCommand("""
    SELECT table_name, column_name, data_type FROM information_schema.columns
    WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
    ORDER BY table_name, ordinal_position
    """, pg))
await using (var r = await cmd.ExecuteReaderAsync())
    while (await r.ReadAsync())
    {
        if (!columns.TryGetValue(r.GetString(0), out var list)) columns[r.GetString(0)] = list = [];
        list.Add((r.GetString(1), r.GetString(2)));
    }
if (columns.Count == 0) throw new InvalidOperationException("The PostgreSQL database has no tables. Run 'dotnet ef database update' first.");

await using var tx = await pg.BeginTransactionAsync();
await Exec("SET LOCAL session_replication_role = replica"); // skip FK checks while tables load in any order
await Exec("TRUNCATE " + string.Join(", ", columns.Keys.Select(Q)) + " RESTART IDENTITY CASCADE");

var report = new List<string>();
foreach (var (table, cols) in columns)
{
    var names = string.Join(", ", cols.Select(c => $"[{c.Name}]"));
    await using var read = new SqlCommand($"SELECT {names} FROM [dbo].[{table}]", sql);
    await using var reader = await read.ExecuteReaderAsync();
    var insert = $"INSERT INTO {Q(table)} ({string.Join(", ", cols.Select(c => Q(c.Name)))}) VALUES ({string.Join(", ", cols.Select((_, i) => "$" + (i + 1)))})";
    var rows = 0;
    while (await reader.ReadAsync())
    {
        await using var ins = new NpgsqlCommand(insert, pg, tx);
        for (var i = 0; i < cols.Count; i++) ins.Parameters.Add(new NpgsqlParameter { Value = Convert(reader.GetValue(i), cols[i].Type) });
        await ins.ExecuteNonQueryAsync();
        rows++;
    }
    report.Add($"{table,-22} {rows,6} rows");
}

// Identity columns: continue numbering after the copied ids.
await using (var cmd = new NpgsqlCommand("""
    SELECT table_name, column_name FROM information_schema.columns
    WHERE table_schema = 'public' AND is_identity = 'YES'
    """, pg, tx))
{
    var identity = new List<(string T, string C)>();
    await using (var r = await cmd.ExecuteReaderAsync()) while (await r.ReadAsync()) identity.Add((r.GetString(0), r.GetString(1)));
    foreach (var (t, c) in identity)
        await Exec($"SELECT setval(pg_get_serial_sequence('{Q(t)}', '{c}'), COALESCE((SELECT MAX({Q(c)}) FROM {Q(t)}), 0) + 1, false)");
}

await tx.CommitAsync();
report.ForEach(Console.WriteLine);
Console.WriteLine("Done: data copied to PostgreSQL.");

async Task Exec(string text) { await using var c = new NpgsqlCommand(text, pg, tx); await c.ExecuteNonQueryAsync(); }

static string Q(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

static object Convert(object value, string pgType) => value switch
{
    DBNull => DBNull.Value,
    DateTimeOffset dto => dto.ToUniversalTime(),                                   // timestamptz accepts UTC only
    DateTime dt when pgType == "date" => DateOnly.FromDateTime(dt),
    DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),                     // app stores DateTime as UTC
    TimeSpan ts when pgType.StartsWith("time") => TimeOnly.FromTimeSpan(ts),
    _ => value,
};

static string? ReadUserSecret(string id, string key)
{
    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", id, "secrets.json");
    if (!File.Exists(path)) return null;
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    return doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() : null;
}
