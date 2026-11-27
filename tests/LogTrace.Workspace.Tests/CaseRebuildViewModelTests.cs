using LogTrace.Engine.Cases;
using LogTrace.Tests.Support;
using Microsoft.Data.Sqlite;

namespace LogTrace.Workspace.Tests;

// 格式較舊的案件：清單標示「（需要重建）」、開啟前提示、同意後備份並重建（ADR-0030 第 6 點，#218；測試資料 tests/fixtures/multi-day，虛構）
public sealed class CaseRebuildViewModelTests : IDisposable
{
    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new();
    readonly List<MainViewModel> _vms = [];

    public void Dispose()
    {
        foreach (var vm in _vms) vm.Dispose();
        _tmp.Dispose();
    }

    string CasesRoot => Path.Combine(_tmp.Path, "Cases");

    MainViewModel NewViewModel()
    {
        var vm = new MainViewModel(new SettingsStore(Path.Combine(_tmp.Path, "settings.json")), _ui, CasesRoot, _ => false);
        _vms.Add(vm);
        return vm;
    }

    async Task<string> OldCase()
    {
        var vm = NewViewModel();
        vm.NewCaseName = "舊案件";
        vm.CreateCaseCommand.Execute(null);
        vm.ImportSource = TempFolder.Fixture("multi-day");
        await vm.ImportCommand.ExecuteAsync(null);
        var folder = vm.CurrentCaseFolder!;
        vm.CloseCaseCommand.Execute(null);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, Case.DatabaseFileName), Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE meta SET value = '2' WHERE key = 'schema_version'";
        cmd.ExecuteNonQuery();
        return folder;
    }

    [Fact]
    public async Task 清單標示需要重建_不同意時不重建也不開啟()
    {
        var folder = await OldCase();
        var vm = NewViewModel();
        var item = Assert.Single(vm.Cases);
        Assert.Equal("舊案件（需要重建）", item.DisplayName);

        vm.SelectedCase = item;
        _ui.ConfirmResult = false;
        await vm.OpenSelectedCaseCommand.ExecuteAsync(null);

        Assert.Contains("備份到", _ui.LastConfirmMessage);
        Assert.Null(vm.CurrentCaseFolder);
        Assert.True(CaseUpgrade.NeedsRebuild(folder));
    }

    [Fact]
    public async Task 同意後備份並重建_然後開啟()
    {
        var folder = await OldCase();
        var vm = NewViewModel();
        vm.SelectedCase = Assert.Single(vm.Cases);
        _ui.ConfirmResult = true;
        await vm.OpenSelectedCaseCommand.ExecuteAsync(null);

        Assert.Equal(folder, vm.CurrentCaseFolder);
        Assert.True(vm.Events.Count > 0);
        Assert.Contains("已重建案件資料庫", vm.Status);
        Assert.False(CaseUpgrade.NeedsRebuild(folder));
        Assert.True(File.Exists(Path.Combine(_tmp.Path, "Backups", Path.GetFileName(folder), Case.DatabaseFileName)));
        Assert.Equal("舊案件", Assert.Single(vm.Cases).DisplayName);
    }

    [Fact]
    public async Task 重建失敗_案件維持需要重建_狀態列說明()
    {
        var folder = await OldCase();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(folder, Case.RawFolderName), "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
            File.Delete(f);
        }
        var vm = NewViewModel();
        vm.SelectedCase = Assert.Single(vm.Cases);
        _ui.ConfirmResult = true;
        await vm.OpenSelectedCaseCommand.ExecuteAsync(null);

        Assert.Null(vm.CurrentCaseFolder);
        Assert.Contains("重建案件資料庫失敗", vm.Status);
        Assert.True(CaseUpgrade.NeedsRebuild(folder));
    }
}
