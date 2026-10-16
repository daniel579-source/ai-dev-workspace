using System.Globalization;
using LogTrace.Engine.Cases;

namespace LogTrace.Workspace;

/// <summary>案件清單的一列。</summary>
/// <param name="recentRank">在最近使用清單中的位置（0 為最近）；不在清單中為 null。</param>
public sealed class CaseListItemViewModel(CaseSummary summary, int? recentRank)
{
    public CaseSummary Summary { get; } = summary;
    public string Folder => Summary.Folder;
    public string Name => Summary.Name;
    public string CreatedAt => Summary.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    public int? RecentRank { get; } = recentRank;
    public bool IsRecent => RecentRank is not null;
}
