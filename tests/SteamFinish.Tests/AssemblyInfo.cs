using Xunit;

// The UI language is global state: LocalizationTests switches it while other classes assert on the
// English strings. Running the classes one at a time keeps that from turning into a flaky failure.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
