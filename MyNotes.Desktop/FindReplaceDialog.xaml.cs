using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit;

namespace MyNotes.Desktop;

public partial class FindReplaceDialog : Window
{
    private readonly TextEditor _editor;
    private int _lastSearchIndex = -1;

    public FindReplaceDialog(TextEditor editor)
    {
        InitializeComponent();
        _editor = editor;

        // Pre-fill with selected text
        if (!string.IsNullOrEmpty(editor.SelectedText))
        {
            FindTextBox.Text = editor.SelectedText;
        }
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void FindTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindNext_Click(sender, e);
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        FindInDirection(forward: true);
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e)
    {
        FindInDirection(forward: false);
    }

    private void FindInDirection(bool forward)
    {
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText))
        {
            StatusText.Text = "Enter search text";
            return;
        }

        var text = _editor.Text;
        var comparison = MatchCaseCheck.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (UseRegexCheck.IsChecked == true)
        {
            FindWithRegex(searchText, forward);
            return;
        }

        if (WholeWordCheck.IsChecked == true)
        {
            searchText = $@"\b{Regex.Escape(searchText)}\b";
            FindWithRegex(searchText, forward);
            return;
        }

        int startIndex = forward
            ? (_editor.SelectionStart + _editor.SelectionLength)
            : (_editor.SelectionStart - 1);

        if (startIndex < 0) startIndex = text.Length - 1;
        if (startIndex >= text.Length) startIndex = 0;

        int index;
        if (forward)
        {
            index = text.IndexOf(searchText, startIndex, comparison);
            if (index < 0)
                index = text.IndexOf(searchText, 0, comparison); // wrap
        }
        else
        {
            index = text.LastIndexOf(searchText, startIndex, comparison);
            if (index < 0)
                index = text.LastIndexOf(searchText, text.Length - 1, comparison); // wrap
        }

        if (index >= 0)
        {
            _editor.Select(index, searchText.Length);
            _editor.ScrollTo(_editor.Document.GetLineByOffset(index).LineNumber, 0);
            _lastSearchIndex = index;
            CountMatches(searchText, comparison);
        }
        else
        {
            StatusText.Text = "No matches found";
        }
    }

    private void FindWithRegex(string pattern, bool forward)
    {
        try
        {
            var options = MatchCaseCheck.IsChecked == true
                ? RegexOptions.None
                : RegexOptions.IgnoreCase;

            var matches = Regex.Matches(_editor.Text, pattern, options);
            if (matches.Count == 0)
            {
                StatusText.Text = "No matches found";
                return;
            }

            var currentPos = forward
                ? _editor.SelectionStart + _editor.SelectionLength
                : _editor.SelectionStart - 1;

            Match? target = null;
            int matchIndex = 0;
            if (forward)
            {
                for (int i = 0; i < matches.Count; i++)
                {
                    if (matches[i].Index >= currentPos)
                    {
                        target = matches[i];
                        matchIndex = i + 1;
                        break;
                    }
                }
                if (target == null)
                {
                    target = matches[0]; // wrap
                    matchIndex = 1;
                }
            }
            else
            {
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    if (matches[i].Index < currentPos)
                    {
                        target = matches[i];
                        matchIndex = i + 1;
                        break;
                    }
                }
                if (target == null)
                {
                    target = matches[^1]; // wrap
                    matchIndex = matches.Count;
                }
            }

            _editor.Select(target.Index, target.Length);
            _editor.ScrollTo(_editor.Document.GetLineByOffset(target.Index).LineNumber, 0);
            StatusText.Text = $"Match {matchIndex} of {matches.Count}";
        }
        catch (RegexParseException)
        {
            StatusText.Text = "Invalid regex pattern";
        }
    }

    private void CountMatches(string searchText, StringComparison comparison)
    {
        int count = 0;
        int idx = 0;
        while ((idx = _editor.Text.IndexOf(searchText, idx, comparison)) >= 0)
        {
            count++;
            idx += searchText.Length;
        }
        StatusText.Text = $"{count} match(es) found";
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_editor.SelectionLength > 0)
        {
            _editor.Document.Replace(_editor.SelectionStart, _editor.SelectionLength, ReplaceTextBox.Text);
        }
        FindInDirection(forward: true);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        var searchText = FindTextBox.Text;
        var replaceText = ReplaceTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        if (UseRegexCheck.IsChecked == true)
        {
            try
            {
                var options = MatchCaseCheck.IsChecked == true
                    ? RegexOptions.None
                    : RegexOptions.IgnoreCase;
                var result = Regex.Replace(_editor.Text, searchText, replaceText, options);
                var count = Regex.Matches(_editor.Text, searchText, options).Count;
                _editor.Document.Text = result;
                StatusText.Text = $"Replaced {count} match(es)";
            }
            catch (RegexParseException)
            {
                StatusText.Text = "Invalid regex pattern";
            }
            return;
        }

        var comparison = MatchCaseCheck.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int replacements = 0;
        int pos = 0;
        _editor.Document.BeginUpdate();
        try
        {
            while (true)
            {
                pos = _editor.Text.IndexOf(searchText, pos, comparison);
                if (pos < 0) break;
                _editor.Document.Replace(pos, searchText.Length, replaceText);
                pos += replaceText.Length;
                replacements++;
            }
        }
        finally
        {
            _editor.Document.EndUpdate();
        }
        StatusText.Text = $"Replaced {replacements} match(es)";
    }
}
