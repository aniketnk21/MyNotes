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
    private readonly Dictionary<long, TabItem> _openTabs = new();
    private readonly DispatcherTimer _autoSaveTimer;
    private double _fontSize = 14;
    private bool _showLineNumbers = true;
    private bool _wordWrap = false;
    private FindReplaceDialog? _findReplaceDialog;
    private string _globalSearchText = "";

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
        UpdateWelcomeVisibility();
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
        // Selection change handled - no action needed unless double click
    }

    private void NoteTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (NoteTree.SelectedItem is TreeViewItem { Tag: NoteDocument doc })
        {
            OpenDocumentTab(doc);
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
        foreach (TabItem tab in EditorTabs.Items)
        {
            if (tab.Content is TextEditor editor)
            {
                var renderer = editor.TextArea.TextView.BackgroundRenderers.OfType<SearchHighlightRenderer>().FirstOrDefault();
                if (renderer != null)
                {
                    editor.TextArea.TextView.BackgroundRenderers.Remove(renderer);
                    editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════
    //  TAB MANAGEMENT
    // ═══════════════════════════════════════════════════

    private void OpenDocumentTab(NoteDocument doc)
    {
        // If already open, switch to it
        if (_openTabs.TryGetValue(doc.Id, out var existingTab))
        {
            EditorTabs.SelectedItem = existingTab;
            return;
        }

        // Reload from DB to get latest content
        var freshDoc = _db.GetDocument(doc.Id);
        if (freshDoc == null) return;

        var editor = CreateEditor();
        editor.Text = freshDoc.Content;
        editor.Tag = freshDoc;

        // Apply syntax highlighting
        ApplySyntaxHighlighting(editor, freshDoc.SyntaxLanguage);

        // Track caret position
        editor.TextArea.Caret.PositionChanged += (s, e) => UpdateStatusBar(editor);

        // Tab header with close button
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var headerText = new TextBlock
        {
            Text = freshDoc.Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        headerPanel.Children.Add(headerText);
        var closeBtn = new Button
        {
            Content = "✕",
            FontSize = 10,
            Padding = new Thickness(4, 1, 4, 1),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = System.Windows.Media.Brushes.Gray,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };

        var tab = new TabItem
        {
            Header = headerPanel,
            Content = editor,
            Tag = freshDoc
        };
        closeBtn.Click += (s, e) => CloseTab(tab);
        headerPanel.Children.Add(closeBtn);

        EditorTabs.Items.Add(tab);
        EditorTabs.SelectedItem = tab;
        _openTabs[freshDoc.Id] = tab;

        UpdateWelcomeVisibility();
        UpdateLanguageCombo(freshDoc.SyntaxLanguage);
        UpdateStatusBar(editor);
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

    private void CloseTab(TabItem tab)
    {
        if (tab.Tag is NoteDocument doc)
        {
            // Save before closing
            SaveDocumentFromTab(tab);
            _openTabs.Remove(doc.Id);
        }
        EditorTabs.Items.Remove(tab);
        UpdateWelcomeVisibility();
    }

    private void CloseCurrentTab()
    {
        if (EditorTabs.SelectedItem is TabItem tab)
        {
            CloseTab(tab);
        }
    }

    private void UpdateWelcomeVisibility()
    {
        WelcomePanel.Visibility = EditorTabs.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EditorTabs.SelectedItem is TabItem { Content: TextEditor editor, Tag: NoteDocument doc })
        {
            UpdateStatusBar(editor);
            UpdateLanguageCombo(doc.SyntaxLanguage);
            HighlightSearchInActiveEditor();
        }
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
            EditorTabs.SelectedItem is TabItem { Content: TextEditor editor, Tag: NoteDocument doc })
        {
            doc.SyntaxLanguage = lang;
            ApplySyntaxHighlighting(editor, lang);
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

    private void SaveDocumentFromTab(TabItem tab)
    {
        if (tab.Content is TextEditor editor && tab.Tag is NoteDocument doc)
        {
            doc.Content = editor.Text;
            doc.UpdatedAt = DateTime.UtcNow;
            _db.SaveDocument(doc);
        }
    }

    private void SaveAllDocuments(bool silent = false)
    {
        foreach (TabItem tab in EditorTabs.Items)
        {
            SaveDocumentFromTab(tab);
        }
        if (!silent)
        {
            StatusInfo.Text = $"All documents saved at {DateTime.Now:HH:mm:ss}";
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
        if (newDoc != null) OpenDocumentTab(newDoc);
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
            // Close any open tabs for documents in this category
            var docsToClose = _openTabs.Keys.ToList();
            foreach (var docId in docsToClose)
            {
                if (_openTabs.TryGetValue(docId, out var tab) && tab.Tag is NoteDocument d && d.CategoryId == cat.Id)
                {
                    EditorTabs.Items.Remove(tab);
                    _openTabs.Remove(docId);
                }
            }
            _db.DeleteCategory(cat.Id);
            LoadTree();
            UpdateWelcomeVisibility();
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

            // Update tab header if open
            if (_openTabs.TryGetValue(doc.Id, out var tab))
            {
                if (tab.Header is StackPanel panel && panel.Children[0] is TextBlock tb)
                {
                    tb.Text = newTitle;
                }
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
            if (_openTabs.TryGetValue(doc.Id, out var tab))
            {
                EditorTabs.Items.Remove(tab);
                _openTabs.Remove(doc.Id);
            }
            _db.DeleteDocument(doc.Id);
            LoadTree();
            UpdateWelcomeVisibility();
            StatusInfo.Text = $"Deleted: {doc.Title}";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedItem is TabItem tab)
        {
            SaveDocumentFromTab(tab);
            StatusInfo.Text = $"Saved at {DateTime.Now:HH:mm:ss}";
        }
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        SaveAllDocuments();
    }

    private void ExportToFile_Click(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedItem is not TabItem { Content: TextEditor editor, Tag: NoteDocument doc }) return;

        var dialog = new SaveFileDialog
        {
            FileName = doc.Title,
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
            DefaultExt = ".txt"
        };
        if (dialog.ShowDialog() == true)
        {
            System.IO.File.WriteAllText(dialog.FileName, editor.Text);
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
        if (newDoc != null) OpenDocumentTab(newDoc);
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
        return EditorTabs.SelectedItem is TabItem { Content: TextEditor editor } ? editor : null;
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
        foreach (TabItem tab in EditorTabs.Items)
        {
            if (tab.Content is TextEditor editor)
                editor.WordWrap = _wordWrap;
        }
    }

    private void ToggleLineNumbers_Click(object sender, RoutedEventArgs e)
    {
        _showLineNumbers = MenuLineNumbers.IsChecked;
        foreach (TabItem tab in EditorTabs.Items)
        {
            if (tab.Content is TextEditor editor)
                editor.ShowLineNumbers = _showLineNumbers;
        }
    }

    private void SetLanguage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string lang })
        {
            if (EditorTabs.SelectedItem is TabItem { Content: TextEditor editor, Tag: NoteDocument doc })
            {
                doc.SyntaxLanguage = lang;
                ApplySyntaxHighlighting(editor, lang);
                UpdateLanguageCombo(lang);
                StatusLanguage.Text = lang == "Plain" ? "Plain Text" : lang;
            }
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
        foreach (TabItem tab in EditorTabs.Items)
        {
            if (tab.Content is TextEditor editor)
                editor.FontSize = _fontSize;
        }
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (FontSizeCombo.SelectedItem is ComboBoxItem { Content: string sizeStr } &&
            double.TryParse(sizeStr, out var size))
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
        foreach (TabItem tab in EditorTabs.Items)
        {
            if (tab.Content is TextEditor editor)
            {
                editor.Background = (System.Windows.Media.Brush)FindResource("EditorBgBrush");
                editor.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
                editor.LineNumbersForeground = (System.Windows.Media.Brush)FindResource("ForegroundDimBrush");
                editor.TextArea.SelectionBrush = (System.Windows.Media.Brush)FindResource("SelectionBrush");
                editor.TextArea.SelectionForeground = null;
            }

            // Update tab header foreground
            if (tab.Header is StackPanel sp)
            {
                foreach (var child in sp.Children)
                {
                    if (child is TextBlock tb)
                        tb.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
                }
            }
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
        _autoSaveTimer.Stop();
        SaveAllDocuments(silent: true);
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