using System;
using System.IO;
using Newtonsoft.Json;
using SolidWorksCadAgent.Core;

namespace SolidWorksCadAgent.AgentHost.Configuration
{
    public sealed class JsonAgentSettingsStore
    {
        private readonly string _path;

        public JsonAgentSettingsStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A settings path is required.", nameof(path));
            _path = Path.GetFullPath(path);
        }

        public AgentSettings Load()
        {
            if (!File.Exists(_path)) return new AgentSettings();

            try
            {
                return ReadAndValidate(_path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is ArgumentException)
            {
                var backupPath = _path + ".bak";
                if (!File.Exists(backupPath)) throw;
                return ReadAndValidate(backupPath);
            }
        }

        public void Save(AgentSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            var temporaryPath = _path + ".tmp";
            var backupPath = _path + ".bak";
            File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(settings, Formatting.Indented));
            try
            {
                if (File.Exists(_path))
                {
                    File.Replace(temporaryPath, _path, backupPath, true);
                }
                else
                {
                    File.Move(temporaryPath, _path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static AgentSettings ReadAndValidate(string path)
        {
            var settings = JsonConvert.DeserializeObject<AgentSettings>(File.ReadAllText(path));
            if (settings == null) throw new JsonSerializationException("The settings file is empty.");
            settings.Validate();
            return settings;
        }
    }
}
