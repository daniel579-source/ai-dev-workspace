using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LogTrace.Engine.Cases;
using LogTrace.Engine.Storage;

namespace LogTrace.Workspace;

/// <summary>
/// 全文索引（ADR-0011 第 1、2、4 點）：開啟或重建案件的事件後，在背景建立；建立期間可以照常瀏覽與搜尋，
/// 尚未建好的日期以掃描搜尋。改變案件內容（匯入、套用新版設定、修改偏移量）或關閉案件前先停止。
/// </summary>
public sealed partial class MainViewModel
{
    CancellationTokenSource? _indexCts;
    Task? _indexTask;

    /// <summary>
    /// 案件磁碟用量的上限（原始 LOG 的倍數，ADR-0011 第 4 點）。測試資料很小時，SQLite 的固定開銷就超過 5 倍，測試會調高。
    /// </summary>
    public double IndexDiskLimitRatio { get; init; } = SearchIndex.DefaultDiskLimitRatio;

    /// <summary>全文索引的狀態（建立進度、已建立、超過磁碟用量上限）；沒有開啟案件時為 null。</summary>
    [ObservableProperty]
    public partial string? IndexStatus { get; private set; }

    /// <summary>背景建立索引中。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildIndexCommand))]
    public partial bool IsIndexing { get; private set; }

    /// <summary>在背景建立尚未建好的索引分片。</summary>
    void StartIndexBuild(Case c)
    {
        StopIndexBuild();
        c.Index.DiskLimitRatio = IndexDiskLimitRatio;
        var cts = _indexCts = new CancellationTokenSource();
        // 進度回報是非同步送達的（沒有畫面執行緒時在執行緒集區）：完成之後才到的進度一律忽略，不蓋掉最後的狀態
        var gate = new object();
        bool finished = false;
        var progress = new Progress<IndexBuildProgress>(p =>
        {
            lock (gate)
            {
                if (!finished && _indexCts == cts && p.Day is not null)
                    IndexStatus = $"全文索引：建立中（{p.BuiltDays}/{p.TotalDays} 天，正在處理 {p.Day}）；尚未建好的日期以掃描搜尋";
            }
        });
        IsIndexing = true;
        IndexStatus = "全文索引：準備中…";
        var ui = SynchronizationContext.Current; // 完成時回到畫面的執行緒更新狀態（測試中沒有時直接更新）
        _indexTask = Task.Run(() =>
        {
            string status;
            try
            {
                var r = c.BuildIndex(progress, cts.Token);
                status = r.LimitReached
                    ? $"全文索引：案件磁碟用量會超過原始 LOG 的 {c.Index.DiskLimitRatio:0.#} 倍，已建立 {r.BuiltDays}/{r.TotalDays} 天，其餘以掃描搜尋"
                    : r.BuiltDays == r.TotalDays ? $"全文索引：已建立（{r.TotalDays} 天）" : $"全文索引：已建立 {r.BuiltDays}/{r.TotalDays} 天，其餘以掃描搜尋";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                status = "全文索引：建立失敗（" + ex.Message + "），以掃描搜尋";
            }
            void Done()
            {
                lock (gate)
                {
                    finished = true;
                    if (_indexCts != cts) return;
                    IndexStatus = status;
                    IsIndexing = false;
                }
            }
            if (ui is null) Done();
            else ui.Post(_ => Done(), null);
        });
    }

    /// <summary>停止背景建立索引並等它結束（已建好的分片保留）。</summary>
    void StopIndexBuild()
    {
        var cts = _indexCts;
        _indexCts = null;
        if (cts is null) return;
        cts.Cancel();
        try
        {
            _indexTask?.Wait();
        }
        catch (AggregateException)
        {
        }
        cts.Dispose();
        _indexTask = null;
        IsIndexing = false;
    }

    /// <summary>等背景建立索引結束（測試用）。</summary>
    public Task WaitForIndexAsync() => _indexTask ?? Task.CompletedTask;

    bool CanRebuildIndex() => _case is not null && !IsBusy;

    /// <summary>刪除全文索引並重新建立（ADR-0011：index\ 屬於可重建的資料）。</summary>
    [RelayCommand(CanExecute = nameof(CanRebuildIndex))]
    void RebuildIndex()
    {
        if (_case is not { } c) return;
        StopIndexBuild();
        try
        {
            c.DeleteIndex();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IndexStatus = "全文索引：刪除失敗（" + ex.Message + "）";
            return;
        }
        StartIndexBuild(c);
    }
}
