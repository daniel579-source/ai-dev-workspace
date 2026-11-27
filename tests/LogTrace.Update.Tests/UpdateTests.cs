using System.IO.Compression;
using System.Text;
using LogTrace.Update;

namespace LogTrace.Update.Tests;

/// <summary>升級（ADR-0030）：以虛構的升級包測試規劃、檢查與套用。</summary>
public sealed class UpdateTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "logtrace-update-" + Guid.NewGuid().ToString("N"));
    readonly ProgramFolder _folder;

    public UpdateTests()
    {
        Directory.CreateDirectory(_root);
        _folder = new ProgramFolder(_root);
        Directory.CreateDirectory(_folder.UpdatesFolder);
        UpdateInstaller.FreeSpace = _ => long.MaxValue;
    }

    public void Dispose()
    {
        UpdateInstaller.FreeSpace = path => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>建立虛構的程式包（內容是文字檔，不是真的程式）。</summary>
    string AppPackage(string version, string runtime = "150.0.1", string? name = null, bool withLauncher = true, string notes = "範例說明")
    {
        var files = new Dictionary<string, string> { ["LogTrace.exe"] = "app " + version, ["web3d/index.html"] = "<html/>" };
        if (withLauncher) files["launcher/LogTrace.exe"] = "launcher " + version;
        return Package(name ?? $"LogTrace-{version}-app.zip",
            new UpdateManifest("app", version, runtime, ["LogTrace.exe", "web3d/index.html"], notes), "app/", files);
    }

    string RuntimePackage(string version, string? name = null) =>
        Package(name ?? $"LogTrace-webview2-{version}-runtime.zip", new UpdateManifest("runtime", version, version, ["msedgewebview2.exe"], ""),
            "runtime/", new() { ["msedgewebview2.exe"] = "runtime " + version, ["Locales/zh-TW.pak"] = new string('x', 5000) });

    string Package(string name, UpdateManifest? manifest, string prefix, Dictionary<string, string> files)
    {
        var path = Path.Combine(_folder.UpdatesFolder, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (manifest is not null)
            using (var w = new StreamWriter(zip.CreateEntry(UpdateManifest.FileName).Open(), Encoding.UTF8))
                w.Write(manifest.ToJson());
        foreach (var (rel, content) in files)
            using (var w = new StreamWriter(zip.CreateEntry(prefix + rel).Open(), Encoding.UTF8))
                w.Write(content);
        return path;
    }

    /// <summary>直接放一個已安裝的版本（相當於首次手動安裝）。</summary>
    void Installed(string version, string runtime = "150.0.1")
    {
        var dir = _folder.AppFolder(Version.Parse(version));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "LogTrace.exe"), "app " + version);
        File.WriteAllText(Path.Combine(dir, ProgramFolder.InstalledFileName), new UpdateManifest("app", version, runtime, ["LogTrace.exe"], "").ToJson());
        var rt = _folder.RuntimeFolder(runtime);
        Directory.CreateDirectory(rt);
        File.WriteAllText(Path.Combine(rt, ProgramFolder.InstalledFileName), new UpdateManifest("runtime", runtime, runtime, ["msedgewebview2.exe"], "").ToJson());
        _folder.SetCurrent(Version.Parse(version));
    }

    IReadOnlyList<string> ScanAndApply()
    {
        var plan = UpdateRunner.Scan(_folder);
        UpdateRunner.RejectAndDescribe(_folder, plan);
        return UpdateRunner.Apply(_folder, UpdateRunner.Installable(plan));
    }

    [Fact]
    public void 從舊版直接跳到最新版_中間版本可以跳過()
    {
        Installed("0.19.0");
        var pkg = AppPackage("0.23.0");
        Assert.Empty(ScanAndApply());
        Assert.Equal(Version.Parse("0.23.0"), _folder.CurrentApp());
        Assert.Equal("app 0.23.0", File.ReadAllText(_folder.AppExe(Version.Parse("0.23.0"))));
        Assert.False(File.Exists(pkg)); // 成功刪除升級包（Q20）
        Assert.True(_folder.HasApp(Version.Parse("0.19.0"))); // 舊版保留（Q10）
    }

    [Fact]
    public void 需要的Runtime還沒到_程式包留在updates_繼續用目前版本()
    {
        Installed("0.19.0");
        var pkg = AppPackage("0.20.0", runtime: "160.0.2");
        var plan = UpdateRunner.Scan(_folder);
        Assert.Equal(PlanAction.WaitForRuntime, Assert.Single(plan).Action);
        Assert.Contains("160.0.2", UpdateRunner.RejectAndDescribe(_folder, plan));
        Assert.Empty(UpdateRunner.Installable(plan));
        Assert.True(File.Exists(pkg));
        Assert.Equal(Version.Parse("0.19.0"), _folder.CurrentApp());

        // Runtime 到了以後一起套用
        RuntimePackage("160.0.2");
        Assert.Empty(ScanAndApply());
        Assert.True(_folder.HasRuntime("160.0.2"));
        Assert.Equal(Version.Parse("0.20.0"), _folder.CurrentApp());
        Assert.True(_folder.HasRuntime("150.0.1")); // 舊 Runtime 保留（Q27）
    }

    [Fact]
    public void 版本較舊的程式包_只解開不切換()
    {
        Installed("0.22.0");
        AppPackage("0.20.0");
        Assert.Empty(ScanAndApply());
        Assert.True(_folder.HasApp(Version.Parse("0.20.0")));
        Assert.Equal(Version.Parse("0.22.0"), _folder.CurrentApp());
    }

    [Fact]
    public void 已經安裝的版本_移到rejected並寫原因()
    {
        Installed("0.22.0");
        AppPackage("0.22.0");
        ScanAndApply();
        var rejected = Directory.GetFiles(_folder.RejectedFolder, "*.zip");
        Assert.Single(rejected);
        Assert.Contains("已經安裝", File.ReadAllText(rejected[0] + ".原因.txt"));
    }

    [Fact]
    public void 同一批有多個新版本_全部解開_最新的改成預設()
    {
        Installed("0.19.0");
        AppPackage("0.21.0");
        AppPackage("0.20.0");
        Assert.Empty(ScanAndApply());
        Assert.True(_folder.HasApp(Version.Parse("0.20.0")));
        Assert.Equal(Version.Parse("0.21.0"), _folder.CurrentApp());
    }

    [Fact]
    public void 不完整的zip與缺少必要檔案_不套用()
    {
        Installed("0.19.0");
        var split = Path.Combine(_folder.UpdatesFolder, "LogTrace-0.20.0-app.zip");
        File.WriteAllBytes(split, Encoding.ASCII.GetBytes("PK\x03\x04 這是分割檔的第一份"));
        Package("missing.zip", new UpdateManifest("app", "0.21.0", "150.0.1", ["LogTrace.exe", "web3d/index.html"], ""), "app/",
            new() { ["LogTrace.exe"] = "x" });
        Package("other.zip", null, "app/", new() { ["LogTrace.exe"] = "x" });
        ScanAndApply();
        var reasons = Directory.GetFiles(_folder.RejectedFolder, "*.原因.txt").Select(File.ReadAllText).ToList();
        Assert.Equal(3, reasons.Count);
        Assert.Contains(reasons, r => r.Contains("不是完整的 zip"));
        Assert.Contains(reasons, r => r.Contains("缺少必要檔案") && r.Contains("web3d/index.html"));
        Assert.Contains(reasons, r => r.Contains("沒有 update.json"));
        Assert.Equal(Version.Parse("0.19.0"), _folder.CurrentApp());
    }

    [Fact]
    public void CRC不符_不切換_刪除解開一半的資料夾()
    {
        Installed("0.19.0");
        var pkg = AppPackage("0.20.0");
        // 改壞 LogTrace.exe 的內容但保留 zip 結構：把 stored 內容的一個位元組改掉
        var bytes = File.ReadAllBytes(pkg);
        var marker = Encoding.UTF8.GetBytes("app 0.20.0");
        using (var zip = ZipFile.Open(pkg, ZipArchiveMode.Update))
        {
            var e = zip.GetEntry("app/LogTrace.exe")!;
            e.Delete();
            var stored = zip.CreateEntry("app/LogTrace.exe", CompressionLevel.NoCompression);
            using var s = stored.Open();
            s.Write(marker);
        }
        bytes = File.ReadAllBytes(pkg);
        var at = IndexOf(bytes, marker);
        Assert.True(at > 0);
        bytes[at] ^= 0x01;
        File.WriteAllBytes(pkg, bytes);

        var errors = ScanAndApply();
        Assert.Contains(errors, e => e.Contains("CRC"));
        Assert.False(_folder.HasApp(Version.Parse("0.20.0")));
        Assert.False(Directory.Exists(_folder.AppFolder(Version.Parse("0.20.0")) + ".partial"));
        Assert.Equal(Version.Parse("0.19.0"), _folder.CurrentApp());
        Assert.Single(Directory.GetFiles(_folder.RejectedFolder, "*.zip"));
    }

    [Fact]
    public void 磁碟空間不足_不解開並說明需要多少()
    {
        Installed("0.19.0");
        AppPackage("0.20.0");
        UpdateInstaller.FreeSpace = _ => 10 * 1024 * 1024;
        var errors = ScanAndApply();
        Assert.Contains(errors, e => e.Contains("磁碟空間不足") && e.Contains("MB"));
        Assert.False(_folder.HasApp(Version.Parse("0.20.0")));
        Assert.Equal(Version.Parse("0.19.0"), _folder.CurrentApp());
    }

    [Fact]
    public void Runtime解開失敗時_需要它的程式包也不套用()
    {
        Installed("0.19.0");
        RuntimePackage("160.0.2");
        AppPackage("0.20.0", runtime: "160.0.2");
        var calls = 0;
        UpdateInstaller.FreeSpace = _ => calls++ == 0 ? 0 : long.MaxValue; // 只有 Runtime 空間不足
        var errors = ScanAndApply();
        Assert.Equal(2, errors.Count);
        Assert.Equal(Version.Parse("0.19.0"), _folder.CurrentApp());
    }

    [Fact]
    public void 不安全的路徑_整包拒絕()
    {
        Installed("0.19.0");
        Package("evil.zip", new UpdateManifest("app", "0.20.0", "150.0.1", ["LogTrace.exe"], ""), "app/",
            new() { ["LogTrace.exe"] = "x", ["../../outside.txt"] = "x" });
        ScanAndApply();
        Assert.False(File.Exists(Path.Combine(_root, "..", "outside.txt")));
        Assert.Contains("不安全的路徑", File.ReadAllText(Directory.GetFiles(_folder.RejectedFolder, "*.原因.txt")[0]));
    }

    [Fact]
    public void launcher_json不見或指向不存在的版本_改用最新且完整的版本()
    {
        Installed("0.19.0");
        Installed("0.20.0");
        _folder.SetCurrent(Version.Parse("0.19.0"));
        Assert.Equal(Version.Parse("0.19.0"), _folder.CurrentApp());
        File.Delete(_folder.StateFile);
        Assert.Equal(Version.Parse("0.20.0"), _folder.CurrentApp());
        _folder.SetCurrent(Version.Parse("0.99.0"));
        Assert.Equal(Version.Parse("0.20.0"), _folder.CurrentApp());
        // 沒有 installed.json 的資料夾不算
        Directory.CreateDirectory(_folder.AppFolder(Version.Parse("0.30.0")));
        File.WriteAllText(_folder.AppExe(Version.Parse("0.30.0")), "x");
        Assert.Equal(Version.Parse("0.20.0"), _folder.CurrentApp());
    }

    [Fact]
    public void 確認畫面_顯示目前版本_新版本與該版說明_太長時截斷()
    {
        Installed("0.19.0");
        AppPackage("0.22.0", notes: new string('說', UpdateRunner.NotesLimit + 50));
        RuntimePackage("150.0.9");
        var plan = UpdateRunner.Scan(_folder);
        var text = UpdateRunner.ConfirmText(UpdateRunner.Installable(plan), _folder.CurrentApp());
        Assert.Contains("0.19.0 → 0.22.0", text);
        Assert.Contains("WebView2 Runtime 150.0.9", text);
        Assert.Contains("0.22.0 更新說明", text);
        Assert.Contains("CHANGELOG.md", text);
        Assert.Contains("稍後", text);
    }

    [Fact]
    public void 啟動程式替換自己_舊的改名_下次刪除()
    {
        var dir = Path.Combine(_root, "self");
        Directory.CreateDirectory(dir);
        var self = Path.Combine(dir, "LogTrace.exe");
        var bundled = Path.Combine(dir, "bundled.exe");
        File.WriteAllText(self, "old launcher");
        File.WriteAllText(bundled, "new launcher");
        Assert.True(LauncherSelfUpdate.Apply(self, bundled));
        Assert.Equal("new launcher", File.ReadAllText(self));
        Assert.True(File.Exists(Path.Combine(dir, LauncherSelfUpdate.OldName)));
        Assert.False(LauncherSelfUpdate.Apply(self, bundled)); // 內容相同不再替換
        LauncherSelfUpdate.CleanupOld(self);
        Assert.False(File.Exists(Path.Combine(dir, LauncherSelfUpdate.OldName)));
    }

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }
}
