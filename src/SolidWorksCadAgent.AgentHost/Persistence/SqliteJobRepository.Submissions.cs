using System;
using System.Data;
using System.Data.SQLite;
using System.Threading;
using System.Threading.Tasks;
using SolidWorksCadAgent.AgentHost.Jobs;
using SolidWorksCadAgent.Contracts.Jobs;
using SolidWorksCadAgent.Core.Ai;

namespace SolidWorksCadAgent.AgentHost.Persistence
{
    public sealed partial class SqliteJobRepository
    {
        public Task<CadJob> FindSubmissionAsync(Guid id, string inputHash, CancellationToken cancellationToken)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested();
            using (var connection = OpenConnection())
                return Task.FromResult(ReadSubmission(connection, null, id, inputHash));
        }

        public Task<SubmissionCreateResult> CreateSubmissionAsync(CadJob job, CadPlanningImage image, string inputHash, CancellationToken cancellationToken)
        {
            ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested(); ValidateJob(job);
            if (string.IsNullOrEmpty(inputHash)) throw new ArgumentException("Submission hash is required.", nameof(inputHash));
            using (var connection = OpenConnection())
            // Serializable transactions acquire the write reservation before the identity read.
            using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
            {
                var existing = ReadSubmission(connection, transaction, job.Id, inputHash);
                if (existing != null) { transaction.Commit(); return Task.FromResult(new SubmissionCreateResult(existing, false)); }
                InsertJob(connection, transaction, job, image, inputHash);
                transaction.Commit(); return Task.FromResult(new SubmissionCreateResult(job, true));
            }
        }

        private static CadJob ReadSubmission(SQLiteConnection connection, SQLiteTransaction transaction, Guid id, string inputHash)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "SELECT SubmissionInputHash FROM Jobs WHERE Id = @Id;";
                Add(command, "@Id", GuidText(id));
                var stored = command.ExecuteScalar();
                if (stored == null) return null;
                if (stored == DBNull.Value || !string.Equals((string)stored, inputHash, StringComparison.Ordinal))
                    throw new JobCoordinatorException("SUBMISSION_CONFLICT", "This submission identity belongs to different input or a legacy job.");
            }
            return ReadJob(connection, transaction, id);
        }
    }
}
