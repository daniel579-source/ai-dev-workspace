// 冒煙測試會實際開啟視窗並操作，同時執行會互相搶焦點，所以依序執行
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
