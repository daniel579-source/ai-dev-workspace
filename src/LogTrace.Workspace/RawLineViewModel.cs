namespace LogTrace.Workspace;

/// <summary>原始 LOG 面板的一行。</summary>
/// <param name="InSelectedEvent">這一行屬於目前選取的事件（從起始行到結束行）。</param>
public sealed record RawLineViewModel(int LineNo, string Text, bool InSelectedEvent);
