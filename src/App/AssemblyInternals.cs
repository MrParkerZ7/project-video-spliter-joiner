using System.Runtime.CompilerServices;

// Expose internal test-support surface (e.g. BulkItemViewModel.CurrentScanTask,
// BulkCutViewModel.WeightedOverall) to the App unit-test assembly, mirroring the Core project's
// InternalsVisibleTo. Keeps those hooks out of the public API while remaining unit-testable.
[assembly: InternalsVisibleTo("VideoSplitJoiner.App.Tests")]
