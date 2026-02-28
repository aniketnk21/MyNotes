using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using Microsoft.Win32;
using MyNotes.Desktop.Models;
using MyNotes.Desktop.Services;

namespace MyNotes.Desktop;

public partial class MainWindow : Window
{
    // ── Routed Commands ─────────────────────────────
    public static readonly RoutedCommand NewNoteCommand = new();
    public static readonly RoutedCommand SaveCommand = new();
    public static readonly RoutedCommand SaveAllCommand = new();
    public static readonly RoutedCommand FindReplaceCommand = new();
    public static readonly RoutedCommand CloseTabCommand = new();

    private readonly DatabaseService _db = DatabaseService.Instance;
    private readonly DispatcherTimer _autoSaveTimer;
    private double _fontSize = 14;
    private bool _showLineNumbers = true;
    private bool _wordWrap = false;
    private FindReplaceDialog? _findReplaceDialog;
    private string _globalSearchText = "";

    // Single-document editor state
    private TextEditor? _singleEditor;
    private NoteDocument? _currentDoc;

    public MainWindow()
    {
        InitializeComponent();

        // Bind commands
        CommandBindings.Add(new CommandBinding(NewNoteCommand, (s, e) => NewNote_Click(s, e)));
        CommandBindings.Add(new CommandBinding(SaveCommand, (s, e) => Save_Click(s, e)));
        CommandBindings.Add(new CommandBinding(SaveAllCommand, (s, e) => SaveAll_Click(s, e)));
        CommandBindings.Add(new CommandBinding(FindReplaceCommand, (s, e) => FindReplace_Click(s, e)));
        CommandBindings.Add(new CommandBinding(CloseTabCommand, (s, e) => CloseCurrentTab()));

        // Auto-save every 30 seconds
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _autoSaveTimer.Tick += (s, e) => SaveAllDocuments(silent: true);
        _autoSaveTimer.Start();

        LoadTree();
        UpdateEditorVisibility(false);
    }

    // ═══════════════════════════════════════════════════
    //  TREE VIEW
    // ═══════════════════════════════════════════════════

    private void LoadTree(string? searchFilter = null)
    {
        NoteTree.Items.Clear();
        var categories = _db.GetCategories();

        foreach (var cat in categories.Where(c => c.ParentId == null))
        {
            var catNode = CreateCategoryNode(cat, categories, searchFilter);
            if (catNode != null)
                NoteTree.Items.Add(catNode);
        }

        // Expand all top-level items
        foreach (TreeViewItem item in NoteTree.Items)
            item.IsExpanded = true;
    }

    private TreeViewItem? CreateCategoryNode(NoteCategory cat, List<NoteCategory> allCategories, string? searchFilter)
    {
        var docs = _db.GetDocumentsByCategory(cat.Id);
        var childCats = allCategories.Where(c => c.ParentId == cat.Id).ToList();

        // Filter docs if search is active
        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            docs = docs.Where(d =>
                d.Title.Contains(searchFilter, StringComparison.OrdinalIgnoreCase) ||
                d.Content.Contains(searchFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var node = new TreeViewItem
        {
            Tag = cat,
            IsExpanded = true,
            FontWeight = FontWeights.SemiBold
        };
        node.Header = CreateTreeHeader($"📁 {cat.Name}", $"{docs.Count} notes");

        // Category context menu
        var catMenu = new ContextMenu();
        var renameItem = new MenuItem { Header = "Rename" };
        renameItem.Click += (s, e) => RenameCategory_Click(cat, node);
        catMenu.Items.Add(renameItem);

        var addNoteItem = new MenuItem { Header = "New Note Here" };
        addNoteItem.Click += (s, e) => CreateNoteInCategory(cat.Id);
        catMenu.Items.Add(addNoteItem);

        var addSubCat = new MenuItem { Header = "New Subcategory" };
        addSubCat.Click += (s, e) =>
        {
            var name = PromptDialog("New Subcategory", "Enter subcategory name:", "New Subcategory");
            if (name != null)
            {
                _db.AddCategory(name, cat.Id);
                LoadTree();
            }
        };
        catMenu.Items.Add(addSubCat);

        catMenu.Items.Add(new Separator());
        var deleteItem = new MenuItem { Header = "Delete Category" };
        deleteItem.Click += (s, e) => DeleteCategory_Click(cat);
        catMenu.Items.Add(deleteItem);
        node.ContextMenu = catMenu;

        // Add child categories
        foreach (var childCat in childCats)
        {
            var childNode = CreateCategoryNode(childCat, allCategories, searchFilter);
            if (childNode != null)
                node.Items.Add(childNode);
        }

        // Add documents
        foreach (var doc in docs)
        {
            var docNode = new TreeViewItem
            {
                Tag = doc,
                FontWeight = FontWeights.Normal
            };
            docNode.Header = CreateTreeHeader($"📝 {doc.Title}", doc.SyntaxLanguage);

            var docMenu = new ContextMenu();
            var renameDoc = new MenuItem { Header = "Rename" };
            renameDoc.Click += (s, e) => RenameDocument_Click(doc, docNode);
            docMenu.Items.Add(renameDoc);

            var deleteDoc = new MenuItem { Header = "Delete" };
            deleteDoc.Click += (s, e) => DeleteDocument_Click(doc);
            docMenu.Items.Add(deleteDoc);
            docNode.ContextMenu = docMenu;

            node.Items.Add(docNode);
        }

        // If searching and no matches in this category, skip it (unless it has child cats with matches)
        if (!string.IsNullOrWhiteSpace(searchFilter) && docs.Count == 0 && node.Items.Count == 0)
            return null;

        return node;
    }

    private static StackPanel CreateTreeHeader(string text, string badge)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        panel.Children.Add(new TextBlock
        {
            Text = badge,
            FontSize = 10,
            Foreground = System.Windows.Media.Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Normal
        });
        return panel;
    }

    private void NoteTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // Handled in MouseLeftButtonUp to avoid firing on keyboard navigation conflicts
    }

    private void NoteTree_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (NoteTree.SelectedItem is TreeViewItem tvi)
        {
            if (tvi.Tag is NoteDocument doc)
            {
                OpenDocument(doc);
            }
            else if (tvi.Tag is NoteCategory cat)
            {
                // On category single-click: open first note in category if available
                var docs = _db.GetDocumentsByCategory(cat.Id);
                if (docs.Count > 0)
                    OpenDocument(docs[0]);
                else
                    // No notes yet - show the welcome panel with nothing loaded
                    UpdateEditorVisibility(false);
            }
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LoadTree(SearchBox.Text);
    }

    private void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _globalSearchText = GlobalSearchBox.Text;
        LoadTree(_globalSearchText);
        HighlightSearchInActiveEditor();
    }

    private void HighlightSearchInActiveEditor()
    {
        if (string.IsNullOrWhiteSpace(_globalSearchText))
        {
            ClearHighlights();
            return;
        }

        if (GetActiveEditor() is TextEditor editor)
        {
            var renderer = editor.TextArea.TextView.BackgroundRenderers.OfType<SearchHighlightRenderer>().FirstOrDefault();
            if (renderer == null)
            {
                renderer = new SearchHighlightRenderer(editor);
                editor.TextArea.TextView.BackgroundRenderers.Add(renderer);
            }
            renderer.SearchText = _globalSearchText;
            editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
        }
    }

    private void ClearHighlights()
    {
        if (_singleEditor != null)
        {
            var renderer = _singleEditor.TextArea.TextView.BackgroundRenderers.OfType<SearchHighlightRenderer>().FirstOrDefault();
            if (renderer != null)
            {
                _singleEditor.TextArea.TextView.BackgroundRenderers.Remove(renderer);
                _singleEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  SINGLE-DOCUMENT EDITOR MANAGEMENT
    // ═══════════════════════════════════════════════════

    private void OpenDocument(NoteDocument doc)
    {
        // Save the currently open document before switching
        SaveCurrentDocument();

        // Reload from DB to get latest content
        var freshDoc = _db.GetDocument(doc.Id);
        if (freshDoc == null) return;

        _currentDoc = freshDoc;

        // Create editor if it doesn't exist yet, otherwise reuse it
        if (_singleEditor == null)
        {
            _singleEditor = CreateEditor();
            _singleEditor.TextArea.Caret.PositionChanged += (s, e) =>
            {
                if (_singleEditor != null) UpdateStatusBar(_singleEditor);
            };
            EditorHost.Content = _singleEditor;
        }

        _singleEditor.Text = freshDoc.Content;
        _singleEditor.Tag = freshDoc;

        // Apply syntax highlighting
        ApplySyntaxHighlighting(_singleEditor, freshDoc.SyntaxLanguage);

        // Update title bar
        EditorTitleText.Text = freshDoc.Title;
        EditorTitleBar.Visibility = Visibility.Visible;

        UpdateEditorVisibility(true);
        UpdateLanguageCombo(freshDoc.SyntaxLanguage);
        UpdateStatusBar(_singleEditor);
        HighlightSearchInActiveEditor();
    }

    private void SaveCurrentDocument()
    {
        if (_singleEditor != null && _currentDoc != null)
        {
            _currentDoc.Content = _singleEditor.Text;
            _currentDoc.UpdatedAt = DateTime.UtcNow;
            _db.SaveDocument(_currentDoc);
        }
    }

    private TextEditor CreateEditor()
    {
        var editor = new TextEditor
        {
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas, Courier New"),
            FontSize = _fontSize,
            ShowLineNumbers = _showLineNumbers,
            WordWrap = _wordWrap,
            Background = (System.Windows.Media.Brush)FindResource("EditorBgBrush"),
            Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush"),
            LineNumbersForeground = (System.Windows.Media.Brush)FindResource("ForegroundDimBrush"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(8, 4, 4, 4),
        };

        editor.TextArea.SelectionBrush = (System.Windows.Media.Brush)FindResource("SelectionBrush");
        editor.TextArea.SelectionForeground = null; // Use syntax colors

        // Enable bracket highlighting and other features
        editor.Options.HighlightCurrentLine = true;
        editor.Options.EnableHyperlinks = true;
        editor.Options.EnableEmailHyperlinks = true;
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 4;
        editor.Options.ShowBoxForControlCharacters = true;

        return editor;
    }

    private void CloseCurrentTab()
    {
        // Save and close the current document
        SaveCurrentDocument();
        _currentDoc = null;
        if (_singleEditor != null)
            _singleEditor.Text = string.Empty;
        UpdateEditorVisibility(false);
        EditorTitleBar.Visibility = Visibility.Collapsed;
    }

    private void UpdateEditorVisibility(bool docOpen)
    {
        WelcomePanel.Visibility = docOpen ? Visibility.Collapsed : Visibility.Visible;
        EditorHost.Visibility = docOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    // ═══════════════════════════════════════════════════
    //  SYNTAX HIGHLIGHTING
    // ═══════════════════════════════════════════════════

    private static readonly Dictionary<string, string> LanguageMap = new()
    {
        ["Plain"] = "",
        ["C#"] = "C#",
        ["XML"] = "XML",
        ["HTML"] = "HTML",
        ["JavaScript"] = "JavaScript",
        ["CSS"] = "CSS",
        ["JSON"] = "Json",
        ["SQL"] = "TSQL",
        ["Python"] = "Python",
        ["Java"] = "Java",
        ["C++"] = "C++",
        ["PHP"] = "PHP",
        ["MarkDown"] = "MarkDown",
    };

    private static void ApplySyntaxHighlighting(TextEditor editor, string language)
    {
        if (LanguageMap.TryGetValue(language, out var hlName) && !string.IsNullOrEmpty(hlName))
        {
            var definition = HighlightingManager.Instance.GetDefinition(hlName);
            editor.SyntaxHighlighting = definition;
        }
        else
        {
            editor.SyntaxHighlighting = null;
        }
    }

    private void UpdateLanguageCombo(string language)
    {
        _updatingLanguageCombo = true;
        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if (item.Tag?.ToString() == language)
            {
                LanguageCombo.SelectedItem = item;
                break;
            }
        }
        _updatingLanguageCombo = false;
    }

    private bool _updatingLanguageCombo;

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _updatingLanguageCombo) return;
        if (LanguageCombo.SelectedItem is ComboBoxItem { Tag: string lang } &&
            _singleEditor != null && _currentDoc != null)
        {
            _currentDoc.SyntaxLanguage = lang;
            ApplySyntaxHighlighting(_singleEditor, lang);
            StatusLanguage.Text = lang == "Plain" ? "Plain Text" : lang;
        }
    }

    // ═══════════════════════════════════════════════════
    //  STATUS BAR
    // ═══════════════════════════════════════════════════

    private void UpdateStatusBar(TextEditor editor)
    {
        var caret = editor.TextArea.Caret;
        StatusLineCol.Text = $"Ln {caret.Line}, Col {caret.Column}";

        var text = editor.Text;
        var wordCount = string.IsNullOrWhiteSpace(text) ? 0 :
            text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
        StatusWordCount.Text = $"Words: {wordCount}";

        if (editor.Tag is NoteDocument doc)
        {
            StatusLanguage.Text = doc.SyntaxLanguage == "Plain" ? "Plain Text" : doc.SyntaxLanguage;
        }
    }

    // ═══════════════════════════════════════════════════
    //  SAVE / LOAD
    // ═══════════════════════════════════════════════════

    private void SaveAllDocuments(bool silent = false)
    {
        SaveCurrentDocument();
        if (!silent)
        {
            StatusInfo.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
        }
    }

    // ═══════════════════════════════════════════════════
    //  MENU / TOOLBAR EVENT HANDLERS
    // ═══════════════════════════════════════════════════

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        // Determine target category
        long categoryId = 0;

        if (NoteTree.SelectedItem is TreeViewItem selected)
        {
            if (selected.Tag is NoteCategory cat)
                categoryId = cat.Id;
            else if (selected.Tag is NoteDocument doc)
                categoryId = doc.CategoryId;
        }

        if (categoryId == 0)
        {
            // Use first category
            var cats = _db.GetCategories();
            if (cats.Count > 0) categoryId = cats[0].Id;
            else
            {
                categoryId = _db.AddCategory("General");
            }
        }

        CreateNoteInCategory(categoryId);
    }

    private void CreateNoteInCategory(long categoryId)
    {
        var title = PromptDialog("New Note", "Enter note title:", "Untitled");
        if (title == null) return;

        var id = _db.AddDocument(categoryId, title);
        LoadTree();

        var newDoc = _db.GetDocument(id);
        if (newDoc != null) OpenDocument(newDoc);
        StatusInfo.Text = $"Created: {title}";
    }

    private void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog("New Category", "Enter category name:", "New Category");
        if (name == null) return;

        long? parentId = null;
        if (NoteTree.SelectedItem is TreeViewItem { Tag: NoteCategory parentCat })
        {
            parentId = parentCat.Id;
        }

        _db.AddCategory(name, parentId);
        LoadTree();
        StatusInfo.Text = $"Created category: {name}";
    }

    private void RenameCategory_Click(NoteCategory cat, TreeViewItem node)
    {
        var newName = PromptDialog("Rename Category", "Enter new name:", cat.Name);
        if (newName != null)
        {
            _db.RenameCategory(cat.Id, newName);
            LoadTree();
        }
    }

    private void DeleteCategory_Click(NoteCategory cat)
    {
        var result = MessageBox.Show(
            $"Delete category '{cat.Name}' and all its notes?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            // If the currently open doc belongs to this category, clear editor
            if (_currentDoc?.CategoryId == cat.Id)
            {
                _currentDoc = null;
                if (_singleEditor != null) _singleEditor.Text = string.Empty;
                UpdateEditorVisibility(false);
                EditorTitleBar.Visibility = Visibility.Collapsed;
            }
            _db.DeleteCategory(cat.Id);
            LoadTree();
            StatusInfo.Text = $"Deleted category: {cat.Name}";
        }
    }

    private void RenameDocument_Click(NoteDocument doc, TreeViewItem node)
    {
        var newTitle = PromptDialog("Rename Note", "Enter new title:", doc.Title);
        if (newTitle != null)
        {
            doc.Title = newTitle;
            _db.SaveDocument(doc);
            LoadTree();

            // Update title bar if this is the currently open doc
            if (_currentDoc?.Id == doc.Id)
            {
                _currentDoc.Title = newTitle;
                EditorTitleText.Text = newTitle;
            }
        }
    }

    private void DeleteDocument_Click(NoteDocument doc)
    {
        var result = MessageBox.Show(
            $"Delete note '{doc.Title}'?",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            // If the deleted note is currently open, clear the editor
            if (_currentDoc?.Id == doc.Id)
            {
                _currentDoc = null;
                if (_singleEditor != null) _singleEditor.Text = string.Empty;
                UpdateEditorVisibility(false);
                EditorTitleBar.Visibility = Visibility.Collapsed;
            }
            _db.DeleteDocument(doc.Id);
            LoadTree();
            StatusInfo.Text = $"Deleted: {doc.Title}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentDocument();
        StatusInfo.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        SaveAllDocuments();
    }

    private void ExportToFile_Click(object sender, RoutedEventArgs e)
    {
        if (_singleEditor == null || _currentDoc == null) return;

        var dialog = new SaveFileDialog
        {
            FileName = _currentDoc.Title,
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dialog.FileName, _singleEditor.Text);
            StatusInfo.Text = $"Exported to: {dialog.FileName}";
        }
    }

    private void ImportFromFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Text Files (*.txt)|*.txt|Code Files (*.cs;*.xml;*.json;*.js;*.html;*.css;*.py;*.java;*.cpp;*.sql)|*.cs;*.xml;*.json;*.js;*.html;*.css;*.py;*.java;*.cpp;*.sql|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var content = System.IO.File.ReadAllText(dialog.FileName);
        var title = System.IO.Path.GetFileName(dialog.FileName);
        var ext = System.IO.Path.GetExtension(dialog.FileName).ToLowerInvariant();

        // Detect language from extension
        var lang = ext switch
        {
            ".cs" => "C#",
            ".xml" or ".xaml" => "XML",
            ".html" or ".htm" => "HTML",
            ".js" => "JavaScript",
            ".css" => "CSS",
            ".json" => "JSON",
            ".sql" => "SQL",
            ".py" => "Python",
            ".java" => "Java",
            ".cpp" or ".c" or ".h" => "C++",
            ".php" => "PHP",
            ".md" => "MarkDown",
            _ => "Plain"
        };

        // Get target category
        long categoryId;
        if (NoteTree.SelectedItem is TreeViewItem { Tag: NoteCategory cat })
            categoryId = cat.Id;
        else
        {
            var cats = _db.GetCategories();
            categoryId = cats.Count > 0 ? cats[0].Id : _db.AddCategory("General");
        }

        var id = _db.AddDocument(categoryId, title, content, lang);
        LoadTree();
        var newDoc = _db.GetDocument(id);
        if (newDoc != null) OpenDocument(newDoc);
        StatusInfo.Text = $"Imported: {title}";
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        SaveAllDocuments(silent: true);
        Close();
    }

    // ── Edit Menu ─────────────────────────────────────

    private TextEditor? GetActiveEditor()
    {
        return _singleEditor;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => GetActiveEditor()?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => GetActiveEditor()?.Redo();
    private void Cut_Click(object sender, RoutedEventArgs e) => GetActiveEditor()?.Cut();
    private void Copy_Click(object sender, RoutedEventArgs e) => GetActiveEditor()?.Copy();
    private void Paste_Click(object sender, RoutedEventArgs e) => GetActiveEditor()?.Paste();
    private void SelectAll_Click(object sender, RoutedEventArgs e) => GetActiveEditor()?.SelectAll();

    private void FindReplace_Click(object sender, RoutedEventArgs e)
    {
        var editor = GetActiveEditor();
        if (editor == null) return;

        if (_findReplaceDialog != null && _findReplaceDialog.IsLoaded)
        {
            _findReplaceDialog.Activate();
            return;
        }

        _findReplaceDialog = new FindReplaceDialog(editor) { Owner = this };
        _findReplaceDialog.Show();
    }

    // ── View Menu ─────────────────────────────────────

    private void ToggleLeftPanel_Click(object sender, RoutedEventArgs e)
    {
        if (MenuTogglePanel.IsChecked)
        {
            LeftPanelColumn.Width = new GridLength(260);
            LeftPanelColumn.MinWidth = 150;
        }
        else
        {
            LeftPanelColumn.Width = new GridLength(0);
            LeftPanelColumn.MinWidth = 0;
        }
    }

    private void ToggleWordWrap_Click(object sender, RoutedEventArgs e)
    {
        _wordWrap = MenuWordWrap.IsChecked;
        if (_singleEditor != null)
            _singleEditor.WordWrap = _wordWrap;
    }

    private void ToggleLineNumbers_Click(object sender, RoutedEventArgs e)
    {
        _showLineNumbers = MenuLineNumbers.IsChecked;
        if (_singleEditor != null)
            _singleEditor.ShowLineNumbers = _showLineNumbers;
    }

    private void SetLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string lang } && _singleEditor != null && _currentDoc != null)
        {
            _currentDoc.SyntaxLanguage = lang;
            ApplySyntaxHighlighting(_singleEditor, lang);
            UpdateLanguageCombo(lang);
            StatusLanguage.Text = lang == "Plain" ? "Plain Text" : lang;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Min(48, _fontSize + 2);
        ApplyFontSize();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = Math.Max(8, _fontSize - 2);
        ApplyFontSize();
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        _fontSize = 14;
        ApplyFontSize();
    }

    private void ApplyFontSize()
    {
        if (_singleEditor != null)
            _singleEditor.FontSize = _fontSize;
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        double size = 0;
        if (FontSizeCombo.SelectedItem is ComboBoxItem item)
            double.TryParse(item.Content?.ToString(), out size);
        else
            double.TryParse(FontSizeCombo.Text, out size);
        if (size > 0)
        {
            _fontSize = size;
            ApplyFontSize();
        }
    }
    // ── Help Menu ─────────────────────────────────────

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "MyNotes v1.0\n\nA modern note-taking application with syntax highlighting.\n\nBuilt with WPF, AvalonEdit, and SQLite.\n\n© 2026",
            "About MyNotes", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        var tm = ThemeManager.Instance;
        tm.ToggleTheme();

        // Update window background/foreground from theme
        Background = (System.Windows.Media.Brush)FindResource("PrimaryBgBrush");
        Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");

        // Update all open editors
        // The original code iterated through EditorTabs.Items, which is no longer applicable for a single editor.
        // We only need to update the single editor if it exists.
        if (_singleEditor != null)
        {
            _singleEditor.Background = (System.Windows.Media.Brush)FindResource("EditorBgBrush");
            _singleEditor.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
            _singleEditor.LineNumbersForeground = (System.Windows.Media.Brush)FindResource("ForegroundDimBrush");
            _singleEditor.TextArea.SelectionBrush = (System.Windows.Media.Brush)FindResource("SelectionBrush");
            _singleEditor.TextArea.SelectionForeground = null;
        }

        // Toggle icon
        ThemeToggleIcon.Text = tm.IsDarkMode ? "🌙" : "☀️";
    }

    // ═══════════════════════════════════════════════════
    //  HELPER: Simple prompt dialog
    // ═══════════════════════════════════════════════════

    private static string? PromptDialog(string title, string message, string defaultValue = "")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#252526")),
            WindowStyle = WindowStyle.ToolWindow
        };

        var panel = new StackPanel { Margin = new Thickness(15) };
        var label = new TextBlock
        {
            Text = message,
            Foreground = System.Windows.Media.Brushes.LightGray,
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 14
        };
        var textBox = new TextBox
        {
            Text = defaultValue,
            FontSize = 14,
            Padding = new Thickness(6, 4, 6, 4),
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#333337")),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3F3F46")),
            CaretBrush = System.Windows.Media.Brushes.White
        };
        textBox.SelectAll();
        textBox.Focus();

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 15, 0, 0)
        };

        string? result = null;
        var okBtn = new Button
        {
            Content = "OK",
            Width = 80,
            Padding = new Thickness(0, 5, 0, 5),
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        okBtn.Click += (s, e) => { result = textBox.Text; dialog.Close(); };

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Width = 80,
            Padding = new Thickness(0, 5, 0, 5),
            IsCancel = true
        };
        cancelBtn.Click += (s, e) => dialog.Close();

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);

        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;

        dialog.ShowDialog();
        return result;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentDocument();
        base.OnClosing(e);
    }
}

// ═══════════════════════════════════════════════════
//  SEARCH HIGHLIGHT RENDERER
// ═══════════════════════════════════════════════════

internal class SearchHighlightRenderer : ICSharpCode.AvalonEdit.Rendering.IBackgroundRenderer
{
    private readonly TextEditor _editor;
    private string _searchText = "";

    public SearchHighlightRenderer(TextEditor editor)
    {
        _editor = editor;
    }

    public string SearchText
    {
        get => _searchText;
        set => _searchText = value ?? "";
    }

    public ICSharpCode.AvalonEdit.Rendering.KnownLayer Layer => ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background;

    public void Draw(ICSharpCode.AvalonEdit.Rendering.TextView textView, System.Windows.Media.DrawingContext drawingContext)
    {
        if (string.IsNullOrWhiteSpace(_searchText) || textView.Document == null) return;

        var text = textView.Document.Text;
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 255, 255, 0));
        var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Orange, 1);

        var index = 0;
        while ((index = text.IndexOf(_searchText, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var segment = new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = index, Length = _searchText.Length };
            foreach (var rect in ICSharpCode.AvalonEdit.Rendering.BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                drawingContext.DrawRectangle(brush, pen, rect);
            }
            index += _searchText.Length;
        }
    }
}