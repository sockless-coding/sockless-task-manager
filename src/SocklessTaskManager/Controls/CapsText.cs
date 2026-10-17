using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace Sockless.TaskManager.Controls;

/// <summary>
/// The stand-in for CSS <c>text-transform: uppercase</c>: write labels in sentence case
/// (<c>Value="Up time"</c>), display them in capitals, and keep the original as the
/// accessible name so screen readers don't spell them out (typography.md → Casing).
/// Styled as a TextBlock.
/// </summary>
public sealed class CapsText : TextBlock
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<CapsText, string?>(nameof(Value));

    static CapsText()
    {
        ValueProperty.Changed.AddClassHandler<CapsText>((text, e) =>
        {
            var value = e.NewValue as string;
            text.Text = value?.ToUpper(CultureInfo.CurrentCulture);
            AutomationProperties.SetName(text, value ?? string.Empty);
        });
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
