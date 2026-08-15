using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PickandPlace2026.Classes
{
    public class AppSettings
    {
        public double Nozzle1Xoffset { get; set; } = -20.0;
        public double Nozzle1Yoffset { get; set; } = 7.2;
        public double Nozzle2Xoffset { get; set; } = 11.82;
        public double Nozzle2Yoffset { get; set; } = 7.22;

        public double ClearHeight { get; set; } = 15;
        public double PickSpeed { get; set; } = 50;

        private static string FilePath =>
            Path.Combine(AppContext.BaseDirectory, "DataFiles", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                }
            }
            catch
            {
                // Missing/corrupt settings file - fall back to defaults rather
                // than fail app startup over it.
            }

            return new AppSettings();
        }

        public void Save()
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(FilePath, json);
        }
    }

    // Source-generated (Serialization) rather than reflection-based JsonSerializer
    // calls - the Release publish profiles set PublishTrimmed (and x86's
    // runtimeconfig.json disables JsonSerializer's reflection fallback outright),
    // so JsonSerializer.Serialize/Deserialize<AppSettings>() with no JsonTypeInfo
    // would risk throwing NotSupportedException in a trimmed build even though it
    // works fine under the debugger.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(AppSettings))]
    internal partial class AppSettingsJsonContext : JsonSerializerContext
    {
    }
}
