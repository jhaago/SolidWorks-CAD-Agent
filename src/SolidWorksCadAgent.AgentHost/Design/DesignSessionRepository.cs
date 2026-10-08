using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using Newtonsoft.Json;
using SolidWorksCadAgent.Contracts.Design;
namespace SolidWorksCadAgent.AgentHost.Design
{
    public sealed class DesignSessionRepository
    {
        private readonly string path;

        public DesignSessionRepository(string databasePath)
        {
            path = Path.GetFullPath(databasePath);
        }

        private SQLiteConnection Open()
        {
            var c = new SQLiteConnection(new SQLiteConnectionStringBuilder
            {
                DataSource = path,
                Version = 3
            }.ConnectionString);
            c.Open();
            return c;
        }

        public void Initialize()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var c = Open())
            using (var q = c.CreateCommand())
            {
                q.CommandText = "CREATE TABLE IF NOT EXISTS DesignSessions(Id TEXT PRIMARY KEY, UpdatedUtc TEXT NOT NULL, Snapshot TEXT NOT NULL)";
                q.ExecuteNonQuery();
            }
        }

        public DesignSession Get(Guid id)
        {
            using (var c = Open())
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT Snapshot FROM DesignSessions WHERE Id=@id";
                q.Parameters.AddWithValue("@id", id.ToString());
                var value = q.ExecuteScalar() as string;
                return value == null ? null : JsonConvert.DeserializeObject<DesignSession>(value);
            }
        }

        public List<DesignSession> List()
        {
            var result = new List<DesignSession>();
            using (var c = Open())
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT Snapshot FROM DesignSessions ORDER BY UpdatedUtc DESC";
                using (var r = q.ExecuteReader()) while (r.Read()) result.Add(JsonConvert.DeserializeObject<DesignSession>(r.GetString(0)));
            }
            return result;
        }

        public void Save(DesignSession session)
        {
            using (var c = Open())
            using (var q = c.CreateCommand())
            {
                q.CommandText = "INSERT OR REPLACE INTO DesignSessions(Id,UpdatedUtc,Snapshot) VALUES(@id,@utc,@snapshot)";
                q.Parameters.AddWithValue("@id", session.Id.ToString());
                q.Parameters.AddWithValue("@utc", session.UpdatedUtc.ToString("O"));
                q.Parameters.AddWithValue("@snapshot", JsonConvert.SerializeObject(session));
                q.ExecuteNonQuery();
            }
        }
    }
}


