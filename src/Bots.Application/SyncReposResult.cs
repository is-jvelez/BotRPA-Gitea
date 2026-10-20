using Bots.Domain;

namespace Bots.Application;

public sealed record SyncReposResult(long JobRunId, int Attempt, JobRunStatus Status, int ReposOk, int ReposFailed);
