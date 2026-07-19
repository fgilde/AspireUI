using Xunit;

// All WebApplicationFactory<Program>-based test classes share one global SQLite DB
// (%LocalAppData%\AspireUI\aspireui.db, since DB_PATH isn't set in tests). Running them
// in parallel races on shared rows (e.g. the single "settings" row). Force them into one
// collection so xUnit runs them sequentially against each other; pure unit tests keep
// parallelizing normally.
[CollectionDefinition("ServerIntegration", DisableParallelization = true)]
public class ServerIntegrationCollection { }
