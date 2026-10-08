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
    RequiresExplicitApproval INTEGER NOT NULL DEFAULT 0,
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

CREATE TABLE IF NOT EXISTS JobInputImages (
    JobId TEXT PRIMARY KEY NOT NULL,
    MediaType TEXT NOT NULL,
    ImageBytes BLOB NOT NULL,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ManagedModels (
    ModelId TEXT PRIMARY KEY NOT NULL,
    ParentModelId TEXT NULL,
    DocumentKind TEXT NOT NULL,
    Status TEXT NOT NULL,
    CustomPropertyKey TEXT NOT NULL,
    CanonicalPath TEXT NULL,
    LastSavedSha256 TEXT NULL,
    CurrentModelRevisionId TEXT NOT NULL,
    ConfigurationKey TEXT NOT NULL,
    SolidWorksRevision TEXT NULL,
    RegistryVersion INTEGER NOT NULL,
    CreatedUtc TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS EntityReferenceBindings (
    ModelId TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    EntityKind TEXT NOT NULL,
    ConfigurationKey TEXT NOT NULL,
    NativeObjectKind TEXT NOT NULL,
    ReferenceFormatVersion INTEGER NOT NULL,
    NativeReferenceBytes BLOB NULL,
    CreatedAtModelRevisionId TEXT NOT NULL,
    LastResolvedModelRevisionId TEXT NULL,
    SemanticFingerprintJson TEXT NULL,
    Status TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL,
    PRIMARY KEY (ModelId, EntityId, ConfigurationKey),
    FOREIGN KEY (ModelId) REFERENCES ManagedModels(ModelId) ON DELETE CASCADE
);

-- V2-only provenance. Historical v1 models and executions are not backfilled.
CREATE TABLE IF NOT EXISTS V2ModelOwnerships (
    ModelId TEXT PRIMARY KEY NOT NULL,
    JobId TEXT NOT NULL,
    FirstRevisionId TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES ManagedModels(ModelId) ON DELETE CASCADE,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE,
    FOREIGN KEY (FirstRevisionId, JobId) REFERENCES Revisions(Id, JobId) ON DELETE CASCADE,
    UNIQUE (ModelId, JobId)
);

CREATE TABLE IF NOT EXISTS V2MutationAttempts (
    Id TEXT PRIMARY KEY NOT NULL,
    JobId TEXT NOT NULL,
    RevisionId TEXT NOT NULL,
    PlanSha256 TEXT NOT NULL,
    StepKey TEXT NOT NULL,
    ModelId TEXT NOT NULL,
    OutputEntityId TEXT NULL,
    ProspectiveModelRevisionId TEXT NOT NULL,
    Status TEXT NOT NULL CHECK (Status IN ('Prepared', 'Applied', 'Uncertain')),
    CreatedUtc TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL,
    FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE,
    FOREIGN KEY (RevisionId, JobId) REFERENCES Revisions(Id, JobId) ON DELETE CASCADE,
    FOREIGN KEY (ModelId, JobId) REFERENCES V2ModelOwnerships(ModelId, JobId) ON DELETE CASCADE,
    UNIQUE (JobId, RevisionId, StepKey),
    UNIQUE (ModelId, ProspectiveModelRevisionId)
);

CREATE INDEX IF NOT EXISTS IX_Revisions_JobId_RevisionNumber
    ON Revisions(JobId, RevisionNumber);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Revisions_Id_JobId
    ON Revisions(Id, JobId);
CREATE INDEX IF NOT EXISTS IX_CommandExecutions_JobId_Revision_Sequence
    ON CommandExecutions(JobId, RevisionNumber, SequenceNumber);
CREATE INDEX IF NOT EXISTS IX_VerificationResults_JobId_Revision
    ON VerificationResults(JobId, RevisionNumber);
CREATE INDEX IF NOT EXISTS IX_Attachments_JobId_Revision
    ON Attachments(JobId, RevisionNumber);
CREATE INDEX IF NOT EXISTS IX_Jobs_UpdatedUtc_Id
    ON Jobs(UpdatedUtc DESC, Id DESC);
CREATE INDEX IF NOT EXISTS IX_ManagedModels_CanonicalPath
    ON ManagedModels(CanonicalPath);
