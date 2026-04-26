// Two test classes in this assembly each launch a real WinUI app instance;
// running them in parallel would race over foreground focus and keyboard
// input. Force them to run sequentially.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
