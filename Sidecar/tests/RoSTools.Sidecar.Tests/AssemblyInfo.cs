using Xunit;

// Test collections run one at a time.
//
// Log.DirectoryOverride is a static, and five fixtures in this assembly set it in
// their constructor and clear it on dispose so nothing writes into the real user
// profile. Run in parallel, those writes interleave: whichever fixture ran its
// constructor last owns the directory, so a test that reads its own log back finds
// another fixture's file, or none. That is a latent flake for every class that sets
// the override and a certain one for the tests that assert on log content - a
// bootstrap redemption, and an exception message that must reach the log and not the
// response body.
//
// The suite is a couple of seconds either way, so determinism is the better trade.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
