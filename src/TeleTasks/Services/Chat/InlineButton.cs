namespace TeleTasks.Services.Chat;

/// <summary>
/// A single button in an inline keyboard row. <see cref="CallbackData"/> is sent back
/// as a synthetic message text when the user taps the button.
/// </summary>
public sealed record InlineButton(string Label, string CallbackData);
