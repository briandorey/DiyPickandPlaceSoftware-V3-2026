using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PickandPlace2026.Classes
{
    // One row of the parts catalog (DataFiles/components.json) - feeder position,
    // pick/place heights and speed, and how a part should be picked/verified.
    // Replaces the old components.xsd/components.xml-backed DataTable row.
    public class Component
    {
        public int ComponentCode { get; set; }
        public string ComponentValue { get; set; } = "";
        public string Package { get; set; } = "";
        public double PlacementHeight { get; set; }
        public double FeederHeight { get; set; }
        public double FeederX { get; set; }
        public double FeederY { get; set; }
        public int PickerNozzle { get; set; }
        public bool VerifywithCamera { get; set; }
        public bool TapeFeeder { get; set; }
        public int FeederID { get; set; }
        public double PlaceSpeed { get; set; }
    }

    // Source-generated (Serialization) rather than reflection-based JsonSerializer
    // calls - the Release publish profiles set PublishTrimmed (and x86's
    // runtimeconfig.json disables JsonSerializer's reflection fallback outright),
    // so JsonSerializer.Serialize/Deserialize<List<Component>>() with no
    // JsonTypeInfo would risk throwing NotSupportedException in a trimmed build
    // even though it works fine under the debugger.
    [JsonSerializable(typeof(List<Component>))]
    internal partial class ComponentJsonContext : JsonSerializerContext
    {
    }
}
