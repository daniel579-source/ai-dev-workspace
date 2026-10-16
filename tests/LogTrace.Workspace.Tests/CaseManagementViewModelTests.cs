using System.Text;
using LogTrace.Tests.Support;

namespace LogTrace.Workspace.Tests;

// 案件管理與重複匯入的畫面邏輯（測試資料：tests/fixtures，虛構）
public sealed class CaseManagementViewModelTests : IDisposable
{
    const string PlcLog = "20300107/PLC-2030-01-07/PLCLOG.txt";

    readonly TempFolder _tmp = new();
    readonly FakeUserInteraction _ui = new();
    readonly string _settingsPath;
    readonly string _defaultRoot;
    readonly List<MainViewModel> _vms = [];

    public CaseManagementViewModelTests()
    {
        _settingsPath = Path.Combine(_tmp.Path, "settings.json");
        _defaultRoot = Path.Combine(_tmp.Path, "Cases");
    }

    public void Dispose()
    {
        foreach (var vm in _vms) vm.Dispose();
        _tmp.Dispose();
    }

    MainViewModel NewViewModel(Func<string, bool>? isInOneDrive = null)
    {
        var vm = new MainViewModel(new SettingsStore(_settingsPath), _ui, _defaultRoot, isInOneDrive ?? (_ => false));
        _vms.Add(vm);
        return vm;
    }

    static async Task CreateAndImport(MainViewModel vm, string name, string source)
    {
        vm.NewCaseName = name;
        vm.CreateCaseCommand.Execute(null);
        vm.ImportSource = source;
        await vm.ImportCommand.ExecuteAsync(null);
    }

    string CopyFixture(string name)
    {
        var dest = Path.Combine(_tmp.Path, "src-" + Guid.NewGuid().ToString("N")[..8]);
        foreach (var f in Directory.EnumerateFiles(TempFolder.Fixture(name), "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(TempFolder.Fixture(name), f));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target);
        }
        return dest;
    }

    [Fact]
    public void 預設存放位置_沒有警告()
    {
        var vm = NewViewModel();

        Assert.Equal(_defaultRoot, vm.CasesRoot);
        Assert.Null(vm.CasesRootWarning);
    }

    [Fact]
    public void 變更存放位置到OneDrive內時_顯示警告_設定保存到下次啟動()
    {
        var oneDrive = Path.Combine(_tmp.Path, "OneDrive - 虛構公司", "Cases");
        var vm = NewViewModel(p => p.Contains("OneDrive"));
        _ui.FolderResult = oneDrive;

        vm.ChangeCasesRootCommand.Execute(null);

        Assert.Equal(oneDrive, vm.CasesRoot);
        Assert.Contains("OneDrive", vm.CasesRootWarning);
        var reopened = NewViewModel(p => p.Contains("OneDrive"));
        Assert.Equal(oneDrive, reopened.CasesRoot);
        Assert.NotNull(reopened.CasesRootWarning);
    }

    [Fact]
    public async Task 案件清單_最近使用的在前_可以重新開啟案件()
    {
        var vm = NewViewModel();
        await CreateAndImport(vm, "甲", TempFolder.Fixture("one-day-plc"));
        await CreateAndImport(vm, "乙", TempFolder.Fixture("two-modules"));

        Assert.Equal(["乙", "甲"], vm.Cases.Select(c => c.Name));
        Assert.All(vm.Cases, c => Assert.True(c.IsRecent));

        vm.SelectedCase = vm.Cases.Single(c => c.Name == "甲");
        await vm.OpenSelectedCaseCommand.ExecuteAsync(null);

        Assert.Equal("甲", vm.CurrentCaseName);
        Assert.Equal(5, vm.Events.Count);
        Assert.Equal(["甲", "乙"], vm.Cases.Select(c => c.Name));
        Assert.Equal(["甲", "乙"], NewViewModel().Cases.Select(c => c.Name)); // 最近使用保存在設定中
    }

    [Fact]
    public async Task 在檔案總管開啟案件資料夾()
    {
        var vm = NewViewModel();
        Assert.False(vm.OpenCaseFolderCommand.CanExecute(null));
        await CreateAndImport(vm, "甲", TempFolder.Fixture("one-day-plc"));

        vm.OpenCaseFolderCommand.Execute(null);

        Assert.Equal([vm.CurrentCaseFolder!], _ui.OpenedFolders);
    }

    [Fact]
    public async Task 關閉案件後_事件清單清空_案件資料夾可以複製()
    {
        var vm = NewViewModel();
        await CreateAndImport(vm, "甲", TempFolder.Fixture("one-day-plc"));
        var folder = vm.CurrentCaseFolder!;

        vm.CloseCaseCommand.Execute(null);

        Assert.Null(vm.CurrentCaseFolder);
        Assert.Empty(vm.Events);
        Assert.Contains("可以複製案件資料夾", vm.Status);
        Assert.Empty(Directory.EnumerateFiles(folder, "*-wal"));
    }

    [Fact]
    public async Task 刪除案件需要確認_取消時不刪除()
    {
        var vm = NewViewModel();
        await CreateAndImport(vm, "甲", TempFolder.Fixture("one-day-plc"));
        var folder = vm.CurrentCaseFolder!;
        vm.SelectedCase = vm.Cases.Single();

        _ui.ConfirmResult = false;
        vm.DeleteSelectedCaseCommand.Execute(null);
        Assert.True(Directory.Exists(folder));
        Assert.Contains("原本的 LOG 檔案不受影響", _ui.LastConfirmMessage);

        _ui.ConfirmResult = true;
        vm.DeleteSelectedCaseCommand.Execute(null);

        Assert.False(Directory.Exists(folder));
        Assert.Null(vm.CurrentCaseFolder); // 開啟中的案件先關閉再刪除
        Assert.Empty(vm.Cases);
        Assert.True(Directory.Exists(TempFolder.Fixture("one-day-plc")));
    }

    [Fact]
    public async Task 重複匯入有內容衝突時_讓使用者逐檔選擇_選擇改用新檔()
    {
        var source = CopyFixture("one-day-plc");
        var vm = NewViewModel();
        await CreateAndImport(vm, "衝突", source);
        var path = Path.Combine(source, PlcLog);
        File.WriteAllText(path, File.ReadAllText(path).Replace("在席 = ON", "在席 = OFF"), Encoding.UTF8);
        _ui.Conflicts = c => c.Select(i => i.RelativePath).ToHashSet();

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(PlcLog, _ui.LastConflicts!.Single().RelativePath);
        Assert.Contains("衝突改用新檔 1", vm.Status);
        Assert.DoesNotContain(vm.Events, e => e.Event.Message.Contains("在席 = ON"));
    }

    [Fact]
    public async Task 重複匯入有內容衝突時_取消匯入則案件不變()
    {
        var source = CopyFixture("one-day-plc");
        var vm = NewViewModel();
        await CreateAndImport(vm, "衝突", source);
        var before = vm.Events.Select(e => e.Event).ToList();
        var path = Path.Combine(source, PlcLog);
        File.WriteAllText(path, File.ReadAllText(path).Replace("在席 = ON", "在席 = OFF"), Encoding.UTF8);
        _ui.Conflicts = _ => null;

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Contains("已取消匯入", vm.Status);
        Assert.Equal(before, vm.Events.Select(e => e.Event));
        Assert.False(Directory.Exists(Path.Combine(vm.CurrentCaseFolder!, "raw", "_versions")));
    }

    [Fact]
    public async Task 重複匯入相同資料_不詢問_顯示略過()
    {
        var vm = NewViewModel();
        await CreateAndImport(vm, "相同", TempFolder.Fixture("one-day-plc"));

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Null(_ui.LastConflicts);
        Assert.Contains("相同略過 1", vm.Status);
        Assert.Equal(5, vm.Events.Count);
    }

    [Fact]
    public async Task 匯入壓縮檔()
    {
        var vm = NewViewModel();

        await CreateAndImport(vm, "壓縮檔", TempFolder.Fixture("multi-day.7z"));

        Assert.Contains("已匯入 7 個檔案（新增 7）", vm.Status);
        Assert.Equal(13, vm.Events.Count);
    }
}
