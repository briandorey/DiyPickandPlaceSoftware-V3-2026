using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace PickandPlace2026.Classes
{
    public class BoardInfo
    {
        public string BoardName { get; set; } = "";
        public double BoardHeight { get; set; }
        public int MotorRunTime { get; set; }
    }

    // One placement row of a board file - which catalog Component to place,
    // where, and whether it's checked to be picked in the current build.
    public class BoardComponent
    {
        public int ComponentCode { get; set; }
        public string ComponentName { get; set; } = "";
        public double PlacementX { get; set; }
        public double PlacementY { get; set; }
        public int PlacementRotate { get; set; }
        public int PlacementNozzle { get; set; }
        public bool Pick { get; set; } = true;
    }

    // Replaces the old DataSet with "BoardInfo"/"Components" tables (built in
    // BoardDesigner, saved/loaded as XML, consumed by PCBBuilder/PcbVisualiserWindow).
    public class Board
    {
        public BoardInfo BoardInfo { get; set; } = new();
        public List<BoardComponent> Components { get; set; } = new();
    }

    [JsonSerializable(typeof(Board))]
    internal partial class BoardJsonContext : JsonSerializerContext
    {
    }
}
