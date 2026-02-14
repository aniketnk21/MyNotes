namespace MyNotes.Desktop.Models;

public class NoteCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = "New Category";
    public long? ParentId { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
