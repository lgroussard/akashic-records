// Lists the recipes of an Akashic database, or adds one from a JSON file.
//   dotnet run --project tools/AddRecipe -- <akashic.db>                 lists the recipes
//   dotnet run --project tools/AddRecipe -- <akashic.db> dump            prints every recipe's ingredients
//   dotnet run --project tools/AddRecipe -- <akashic.db> <recipe.json>   adds the recipe
// The JSON holds Title, Category, Ingredients, Instructions, Notes, Tags (same fields as the recipe page).
// A recipe whose title already exists is left alone.
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length is < 1 or > 2 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: AddRecipe <akashic.db> [recipe.json]");
    return 1;
}

using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = args[0] }.ToString());
connection.Open();

if (args.Length == 2 && args[1] == "dump")
{
    using var dump = connection.CreateCommand();
    dump.CommandText = "SELECT Id, Title, Ingredients FROM Recipe ORDER BY Id;";
    using var rows = dump.ExecuteReader();
    while (rows.Read())
    {
        Console.WriteLine($"=== {rows.GetInt32(0)} {rows.GetString(1)}");
        Console.WriteLine(rows.GetString(2));
    }
    return 0;
}

if (args.Length == 1)
{
    using var list = connection.CreateCommand();
    list.CommandText = "SELECT Id, Title, Category, Tags, Servings FROM Recipe ORDER BY Id;";
    using var reader = list.ExecuteReader();
    while (reader.Read())
    {
        Console.WriteLine($"{reader.GetInt32(0)}\t{reader.GetString(1)}\t[{reader.GetString(2)}]\t{reader.GetString(3)}");
    }
    return 0;
}

var recipe = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[1]))!;
string Field(string name) => (recipe.GetValueOrDefault(name) ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");

var title = Field("Title").Trim();
if (title.Length == 0)
{
    Console.Error.WriteLine("The recipe needs a Title.");
    return 1;
}

using var exists = connection.CreateCommand();
exists.CommandText = "SELECT COUNT(*) FROM Recipe WHERE Title = $title COLLATE NOCASE;";
exists.Parameters.AddWithValue("$title", title);
if ((long)exists.ExecuteScalar()! > 0)
{
    Console.WriteLine($"Already there, nothing added: {title}");
    return 0;
}

using var insert = connection.CreateCommand();
insert.CommandText =
    """
    INSERT INTO Recipe (Title, Category, Ingredients, Instructions, Notes, Tags, CoverImagePath, CreatedAt)
    VALUES ($title, $category, $ingredients, $instructions, $notes, $tags, NULL, $createdAt);
    SELECT last_insert_rowid();
    """;
insert.Parameters.AddWithValue("$title", title);
insert.Parameters.AddWithValue("$category", Field("Category"));
insert.Parameters.AddWithValue("$ingredients", Field("Ingredients"));
insert.Parameters.AddWithValue("$instructions", Field("Instructions"));
insert.Parameters.AddWithValue("$notes", Field("Notes"));
insert.Parameters.AddWithValue("$tags", Field("Tags"));
insert.Parameters.AddWithValue("$createdAt", DateTime.Now.ToString("O"));
Console.WriteLine($"Added recipe {(long)insert.ExecuteScalar()!}: {title}");
return 0;
