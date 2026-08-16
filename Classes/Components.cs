using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace PickandPlace2026.Classes
{
    public class Components
    {
        private List<Component> components = new();

        private static string DataFilesPath =>
            Path.Combine(AppContext.BaseDirectory, "DataFiles");

        private static string ComponentsFile =>
            Path.Combine(DataFilesPath, "components.json");

        public List<Component> LoadComponents()
        {
            string json = File.ReadAllText(ComponentsFile);
            components = JsonSerializer.Deserialize(json, ComponentJsonContext.Default.ListComponent)
                ?? new List<Component>();
            return components;
        }

        public void SaveComponents(List<Component> componentsToSave)
        {
            components = componentsToSave;
            string json = JsonSerializer.Serialize(components, ComponentJsonContext.Default.ListComponent);
            File.WriteAllText(ComponentsFile, json);
        }

        public void CheckComponentTable()
        {
            if (components.Count == 0)
            {
                LoadComponents();
            }
        }

        private Component? Find(string fid)
        {
            CheckComponentTable();
            if (!int.TryParse(fid, out int componentCode)) return null;
            return components.FirstOrDefault(c => c.ComponentCode == componentCode);
        }

        public string GetComponentValue(string fid)
        {
            return Find(fid)?.ComponentValue ?? "";
        }

        public string GetComponentPackage(string fid)
        {
            return Find(fid)?.Package ?? "";
        }

        public double GetPlacementHeight(string fid)
        {
            Component? c = Find(fid);
            if (c == null) return 0.0;
            return c.PlacementHeight - GetNozzleLengthOffset(c.FeederID);
        }

        public double GetFeederHeight(string fid)
        {
            Component? c = Find(fid);
            if (c == null) return 0.0;
            return c.FeederHeight - GetNozzleLengthOffset(c.FeederID);
        }

        private static double GetNozzleLengthOffset(int feederID)
        {
            AppSettings settings = ((App)Application.Current).Settings;
            return feederID >= 20 ? settings.AAxisNozzleLengthOffset : settings.ZAxisNozzleLengthOffset;
        }

        public double GetPlaceSpeed(string fid, double feedrate)
        {
            Component? component = Find(fid);
            return component != null ? component.PlaceSpeed : feedrate;
        }

        public double GetFeederX(string fid)
        {
            return Find(fid)?.FeederX ?? 0.0;
        }

        public double GetFeederY(string fid)
        {
            return Find(fid)?.FeederY ?? 0.0;
        }

        public int GetPickerNozzle(string fid)
        {
            return Find(fid)?.PickerNozzle ?? 0;
        }

        public int GetFeederID(string fid)
        {
            return Find(fid)?.FeederID ?? 0;
        }

        public bool GetComponentVerifywithCamera(string fid)
        {
            return Find(fid)?.VerifywithCamera ?? false;
        }

        public bool GetComponentTapeFeeder(string fid)
        {
            return Find(fid)?.TapeFeeder ?? false;
        }
    }
}
