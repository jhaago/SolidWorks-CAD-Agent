PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;
PRAGMA busy_timeout = 5000;

CREATE TABLE IF NOT EXISTS Jobs (
    Id TEXT PRIMARY KEY NOT NULL,
    Prompt TEXT NOT NULL,
    State INTEGER NOT NULL,
    PlanValidated INTEGER NOT NULL,
    HasUnresolvedAmbiguity INTEGER NOT NULL,
    AmbiguityMessage TEXT NULL,
    OverwriteRequested INTEGER NOT NULL,
    OverwriteAuthorized INTEGER NOT NULL,
    IsSimulated INTEGER NOT NULL DEFAULT 0,
    OutputPath TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Revisions (
    Id TEXT PRIMARY KEY NOT NULL,
    JobId TEXT NOT NULL,
    RevisionNumber INTEGER NOT NULL,
    Prompt TEXT NOT NULL,
    InterpretationJson TEXT NULL,
    PlanJson TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE,
    UNIQUE (JobId, RevisionNumber)
);

CREATE TABLE IF NOT EXISTS CommandExecutions (
    Id TEXT PRIMARY KEY NOT NULL,
    JobId TEXT NOT NULL,
    RevisionNumber INTEGER NOT NULL,
    SequenceNumber INTEGER NOT NULL,
    CommandName TEXT NOT NULL,
    ParametersJson TEXT NULL,
    Success INTEGER NOT NULL,
    ResultJson TEXT NULL,
    ErrorCode TEXT NULL,
    ErrorMessage TEXT NULL,
    StartedUtc TEXT NOT NULL,
    CompletedUtc TEXT NOT NULL,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE,
    UNIQUE (JobId, RevisionNumber, SequenceNumber)
);

CREATE TABLE IF NOT EXISTS VerificationResults (
    Id TEXT PRIMARY KEY NOT NULL,
    JobId TEXT NOT NULL,
    RevisionNumber INTEGER NOT NULL,
    CheckName TEXT NOT NULL,
    Passed INTEGER NOT NULL,
    ExpectedJson TEXT NULL,
    ActualJson TEXT NULL,
    CreatedUtc TEXT NOT NULL,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Attachments (
    Id TEXT PRIMARY KEY NOT NULL,
    JobId TEXT NOT NULL,
    RevisionNumber INTEGER NOT NULL,
    Kind TEXT NOT NULL,
    Path TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_Revisions_JobId_RevisionNumber
    ON Revisions(JobId, RevisionNumber);
CREATE INDEX IF NOT EXISTS IX_CommandExecutions_JobId_Revision_Sequence
    ON CommandExecutions(JobId, RevisionNumber, SequenceNumber);
CREATE INDEX IF NOT EXISTS IX_VerificationResults_JobId_Revision
    ON VerificationResults(JobId, RevisionNumber);
CREATE INDEX IF NOT EXISTS IX_Attachments_JobId_Revision
    ON Attachments(JobId, RevisionNumber);
CREATE INDEX IF NOT EXISTS IX_Jobs_UpdatedUtc_Id
    ON Jobs(UpdatedUtc DESC, Id DESC);
