namespace MyNotes.Desktop.Models;

public class NoteDocument
{
    public long Id { get; set; }
    public long CategoryId { get; set; }
    public string Title { get; set; } = "Untitled";
    public string Content { get; set; } = string.Empty;
    public string SyntaxLanguage { get; set; } = "Plain";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
