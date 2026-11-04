namespace winuitest.Models;

/// <summary>An enabled audio output endpoint; the system-default option is supplied by the UI.</summary>
public sealed record AudioOutputOption(string Id, string Name, bool IsSelected);
