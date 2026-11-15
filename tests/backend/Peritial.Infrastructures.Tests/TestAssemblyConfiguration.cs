using Xunit;

// All integration tests share the same PostgreSQL database. Each test
// re-applies idempotent DDL and some tests hold long-running RLS transactions.
// Running those test bodies concurrently can deadlock on ALTER POLICY/table
// locks even when individual bootstrap calls use an advisory lock.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
