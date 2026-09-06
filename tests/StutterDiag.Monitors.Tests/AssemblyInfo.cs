using Xunit;

// The Heartbeat tests read GC.CollectionCount deltas inside HeartbeatStutterDetector.Measure to
// suppress self-GC pauses. Running tests single-threaded in this assembly keeps another test
// thread's allocations from perturbing that delta.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
