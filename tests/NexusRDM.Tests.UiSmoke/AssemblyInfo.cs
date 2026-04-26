// Every test in this assembly drives a real WinUI app via FlaUI/UIA — running
// any two at the same time races over foreground focus, the keyboard queue,
// and the same NexusRDM.exe HWNDs. Three layers of "do not parallelize":
//   1. DisableTestParallelization: collections never run concurrently.
//   2. MaxParallelThreads = 1: only one xunit worker thread, period.
//   3. Each test class is decorated with [Collection("UI smoke")]
//      (defined in UiSmokeCollection.cs) so they all share one collection
//      and xunit can't reorder them into anything overlapping.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]
