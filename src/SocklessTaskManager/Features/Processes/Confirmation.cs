using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Processes;

/// <summary>
/// A destructive action waiting for a yes (components.md → Dialogs): the title names what will be
/// destroyed, the message says what that costs, and a warning stands out when it's worse than usual.
/// </summary>
public sealed class Confirmation
{
    internal Confirmation(string title, string message, string? warning, string confirmText, Action confirm, Action cancel)
    {
        Title = title;
        Message = message;
        Warning = warning;
        ConfirmText = confirmText;
        ConfirmCommand = new RelayCommand(confirm);
        CancelCommand = new RelayCommand(cancel);
    }

    public string Title { get; }

    public string Message { get; }

    public string? Warning { get; }

    public bool HasWarning => Warning is not null;

    public string ConfirmText { get; }

    public ICommand ConfirmCommand { get; }

    public ICommand CancelCommand { get; }
}
