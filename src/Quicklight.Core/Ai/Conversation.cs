namespace Quicklight.Core.Ai;

/// <summary>What the AI window shows: one state at a time, driven by <see cref="AiSession.StateChanged"/>.</summary>
public enum AiState { Idle, Working, Searching, Answer, Error, Loading }

public enum ChatRole { User, Assistant }

/// <param name="Kind">"file" or "image".</param>
public sealed record Attachment(string Kind, string Path, string? Name = null);

// The classes below are plain bags for System.Text.Json (conversations.json): public setters, parameterless constructors.

public sealed class ChatMessage
{
    public ChatRole Role { get; set; }
    public string Text { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public List<Attachment> Attachments { get; set; } = [];
    public bool IsError { get; set; }
}

public sealed class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public bool Pinned { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public List<ChatMessage> Messages { get; set; } = [];
}
