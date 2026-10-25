using System.Windows;
using WinTools.Core.Jobs;
using WinTools.Windows;

namespace WinTools.Dialogs;

/// <summary>
/// A password to type (V2 PR 2.2, ADR-0026): once to use an existing one, twice for a new one. The policy is applied
/// here with Windows' NFKC so the user hears at once what is wrong; the host and the worker apply it again. Enter is OK,
/// Esc cancels; closing it any other way is a cancel. The boxes are cleared when it closes.
/// </summary>
public partial class SecretWindow : Window
{
    private readonly SecretRequest _request;

    /// <summary>The password as typed (the host normalizes it again), or null when cancelled.</summary>
    public string? Secret { get; private set; }

    public SecretWindow(SecretRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = request.Title;
        Heading.Text = request.Title;
        ConfirmPanel.Visibility = request.Confirm ? Visibility.Visible : Visibility.Collapsed;
        Hint.Text = request.Confirm
            ? "WinTools keeps the password nowhere. Without it the PDF cannot be opened, so keep it safe."
            : "The password that opens the PDF. WinTools keeps it nowhere.";
        Loaded += (_, _) =>
        {
            WindowPlacement.NearPoint(this, _request.X, _request.Y);
            Activate();
            First.Focus();
        };
        Closed += (_, _) =>
        {
            First.Clear();
            Second.Clear();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // A new password must already be in its normalized form; an existing one is taken as typed (ADR-0026).
        var problem = _request.Confirm ? PasswordPolicy.CheckNew(First.Password, UnicodeText.ToFormKC).Problem
            : PasswordPolicy.CheckExisting(First.Password, UnicodeText.ToFormKC).Problem;
        if (problem is null && _request.Confirm && !string.Equals(First.Password, Second.Password, StringComparison.Ordinal))
            problem = "The two passwords differ.";
        if (problem is not null)
        {
            Problem.Text = problem;
            Problem.Visibility = Visibility.Visible;
            First.Focus();
            First.SelectAll();
            return;
        }
        Secret = First.Password;
        Close();
    }
}
