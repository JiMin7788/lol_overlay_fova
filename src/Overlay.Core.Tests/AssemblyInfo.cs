using Xunit;

// EventBus (M15) is a static, process-wide bus. Running test classes in parallel
// (xUnit's default) means one test's EventBus.ResetForTests()/Subscribe calls can
// stomp on another test's in-flight assertions, since they all share the same static
// state. Disabling collection parallelization makes the test suite deterministic;
// it has no bearing on EventBus's own thread-safety (which is covered by the
// UI.*-dispatch-thread and ordering tests running real concurrent dispatch internally).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
