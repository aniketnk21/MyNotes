using System.IO;
using Microsoft.Data.Sqlite;
using MyNotes.Desktop.Models;

namespace MyNotes.Desktop.Services;

public class DatabaseService
{
    private static DatabaseService? _instance;
    private readonly string _connectionString;

    public static DatabaseService Instance => _instance ??= new DatabaseService();

    private DatabaseService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyNotes");
        Directory.CreateDirectory(appDataPath);
        var dbPath = Path.Combine(appDataPath, "mynotes.db");
        _connectionString = $"Data Source={dbPath}";
    }

    private SqliteConnection GetConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void InitializeDatabase()
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Categories (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL DEFAULT 'New Category',
                ParentId INTEGER NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (ParentId) REFERENCES Categories(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Documents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CategoryId INTEGER NOT NULL,
                Title TEXT NOT NULL DEFAULT 'Untitled',
                Content TEXT NOT NULL DEFAULT '',
                SyntaxLanguage TEXT NOT NULL DEFAULT 'Plain',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (CategoryId) REFERENCES Categories(Id) ON DELETE CASCADE
            );
        ";
        cmd.ExecuteNonQuery();

        // Ensure at least one default category exists
        cmd.CommandText = "SELECT COUNT(*) FROM Categories";
        var count = (long)cmd.ExecuteScalar()!;
        if (count == 0)
        {
            cmd.CommandText = "INSERT INTO Categories (Name, SortOrder) VALUES ('General', 0)";
            cmd.ExecuteNonQuery();
        }
    }

    // ── Categories ────────────────────────────────────────────

    public List<NoteCategory> GetCategories()
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Categories ORDER BY SortOrder, Name";
        using var reader = cmd.ExecuteReader();
        var list = new List<NoteCategory>();
        while (reader.Read())
        {
            list.Add(new NoteCategory
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                SortOrder = reader.GetInt32(3),
                CreatedAt = DateTime.Parse(reader.GetString(4)),
                UpdatedAt = DateTime.Parse(reader.GetString(5))
            });
        }
        return list;
    }

    public long AddCategory(string name, long? parentId = null)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO Categories (Name, ParentId) VALUES (@name, @parentId);
                            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@parentId", (object?)parentId ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    public void RenameCategory(long id, string newName)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Categories SET Name = @name, UpdatedAt = datetime('now') WHERE Id = @id";
        cmd.Parameters.AddWithValue("@name", newName);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteCategory(long id)
    {
        using var conn = GetConnection();
        // Enable foreign keys for cascade delete
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        pragma.ExecuteNonQuery();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Documents WHERE CategoryId = @id; DELETE FROM Categories WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // ── Documents ─────────────────────────────────────────────

    public List<NoteDocument> GetDocumentsByCategory(long categoryId)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Documents WHERE CategoryId = @catId ORDER BY Title";
        cmd.Parameters.AddWithValue("@catId", categoryId);
        using var reader = cmd.ExecuteReader();
        var list = new List<NoteDocument>();
        while (reader.Read())
        {
            list.Add(ReadDocument(reader));
        }
        return list;
    }

    public NoteDocument? GetDocument(long id)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Documents WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadDocument(reader) : null;
    }

    public long AddDocument(long categoryId, string title, string content = "", string syntaxLanguage = "Plain")
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO Documents (CategoryId, Title, Content, SyntaxLanguage) 
                            VALUES (@catId, @title, @content, @lang);
                            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@catId", categoryId);
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@lang", syntaxLanguage);
        return (long)cmd.ExecuteScalar()!;
    }

    public void SaveDocument(NoteDocument doc)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE Documents 
                            SET Title = @title, Content = @content, SyntaxLanguage = @lang, UpdatedAt = datetime('now')
                            WHERE Id = @id";
        cmd.Parameters.AddWithValue("@title", doc.Title);
        cmd.Parameters.AddWithValue("@content", doc.Content);
        cmd.Parameters.AddWithValue("@lang", doc.SyntaxLanguage);
        cmd.Parameters.AddWithValue("@id", doc.Id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteDocument(long id)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Documents WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public List<NoteDocument> SearchDocuments(string query)
    {
        using var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT * FROM Documents 
                            WHERE Title LIKE @q OR Content LIKE @q 
                            ORDER BY UpdatedAt DESC";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        using var reader = cmd.ExecuteReader();
        var list = new List<NoteDocument>();
        while (reader.Read())
        {
            list.Add(ReadDocument(reader));
        }
        return list;
    }

    private static NoteDocument ReadDocument(SqliteDataReader reader)
    {
        return new NoteDocument
        {
            Id = reader.GetInt64(0),
            CategoryId = reader.GetInt64(1),
            Title = reader.GetString(2),
            Content = reader.GetString(3),
            SyntaxLanguage = reader.GetString(4),
            CreatedAt = DateTime.Parse(reader.GetString(5)),
            UpdatedAt = DateTime.Parse(reader.GetString(6))
        };
    }
}
