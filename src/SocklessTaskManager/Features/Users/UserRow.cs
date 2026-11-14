using System.Windows.Input;
using Sockless.TaskManager.Core;

namespace Sockless.TaskManager.Features.Users;

/// <summary>
/// One line of the Users table: a signed-in session, or an account that runs processes. Kept for as
/// long as its key is listed and updated in place, so the table only re-renders what changed.
/// </summary>
public sealed class UserRow : ObservableObject
{
    private string _name = string.Empty;
    private string _caption = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSuccess;
    private string _sessionText = string.Empty;
    private string _sinceText = string.Empty;
    private string _processesText = string.Empty;
    private string _cpuText = string.Empty;
    private string _memoryText = string.Empty;
    private bool _isSelected;

    internal UserRow(string key, Action<UserRow> select)
    {
        Key = key;
        SelectCommand = new RelayCommand(() => select(this));
    }

    internal string Key { get; }

    /// <summary>What the row stands for, as of the last sample.</summary>
    internal UserGroup? Group { get; set; }

    public ICommand SelectCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public string Caption
    {
        get => _caption;
        set
        {
            if (Set(ref _caption, value))
                OnPropertyChanged(nameof(HasCaption));
        }
    }

    public bool HasCaption => _caption.Length > 0;

    /// <summary>A word, always: Active, Locked, Service…</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (Set(ref _statusText, value))
                OnPropertyChanged(nameof(AccessibleName));
        }
    }

    public bool IsSuccess
    {
        get => _isSuccess;
        set => Set(ref _isSuccess, value);
    }

    public string SessionText
    {
        get => _sessionText;
        set => Set(ref _sessionText, value);
    }

    public string SinceText
    {
        get => _sinceText;
        set => Set(ref _sinceText, value);
    }

    public string ProcessesText
    {
        get => _processesText;
        set => Set(ref _processesText, value);
    }

    public string CpuText
    {
        get => _cpuText;
        set => Set(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        set => Set(ref _memoryText, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string AccessibleName => $"{_name}, {_statusText}";
}
