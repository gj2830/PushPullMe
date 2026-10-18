using System.Windows;
using System.Windows.Controls;
using PushPullMe.Core;
using PushPullMe.Editor.Session;

namespace PushPullMe;

/// <summary>
/// The texts of the window as written in its XAML (Dutch) and in English: in English, every tooltip, label and button
/// text is looked up here when the window opens.
/// </summary>
internal static class XamlTexts
{


    /// <summary>The window's texts (tooltips, labels, button texts) in the language chosen, all through it (from either
    /// language: it can be switched while the app runs).</summary>
    public static void Apply(DependencyObject root) => Walk(root);

    private static string Tr(string s) => UiTexts.Tr(s);

    private static void Walk(object node)
    {
        if (node is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = Tr(tip);
        switch (node)
        {
            case TextBlock tb when tb.Text is { Length: > 0 }: tb.Text = Tr(tb.Text); break;
            case ContentControl cc when cc.Content is string text: cc.Content = Tr(text); break;
        }
        if (node is DependencyObject d)
            foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
    }
}
