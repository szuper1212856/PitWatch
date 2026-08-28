using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace PitWatch.Gui;

public partial class UpdateNotesWindow : Window
{
    /// <summary>True if the user chose to update from this window.</summary>
    public bool UpdateRequested { get; private set; }

    public UpdateNotesWindow(string newVersion, string? notesMarkdown)
    {
        InitializeComponent();

        VersionHeader.Text = $"PitWatch {newVersion}";
        CurrentVersionText.Text = $"You're on {PitWatch.AppInfo.Version}";

        NotesText.Text = string.IsNullOrWhiteSpace(notesMarkdown)
            ? "No release notes were included with this update.\n\n"
              + "It's still safe to install - updates are downloaded from the official PitWatch releases page."
            : TidyMarkdown(notesMarkdown);
    }

    /// <summary>
    /// Release notes arrive as markdown, but this window renders plain text - so rather
    /// than showing raw "## " and "* " markers, this strips the syntax and converts
    /// bullets into something readable. Deliberately simple: a full markdown renderer
    /// would be a lot of machinery for a short changelog.
    /// </summary>
    private static string TidyMarkdown(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Headings become plain lines with spacing around them.
            if (line.StartsWith("#"))
            {
                var heading = line.TrimStart('#').Trim();
                if (heading.Length == 0) continue;
                if (output.Count > 0) output.Add("");
                output.Add(heading.ToUpperInvariant());
                continue;
            }

            // Bullets get a real bullet character.
            if (Regex.IsMatch(line, @"^\s*[-*+]\s+"))
            {
                line = Regex.Replace(line, @"^\s*[-*+]\s+", "  •  ");
            }

            // Strip inline emphasis and code markers, and turn [text](url) into just text.
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
            line = line.Replace("**", "").Replace("`", "");

            output.Add(line);
        }

        // Collapse runs of blank lines so the layout stays tight.
        var tidied = new List<string>();
        foreach (var line in output)
        {
            if (line.Length == 0 && tidied.Count > 0 && tidied[^1].Length == 0) continue;
            tidied.Add(line);
        }

        return string.Join("\n", tidied).Trim();
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        UpdateRequested = true;
        DialogResult = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
