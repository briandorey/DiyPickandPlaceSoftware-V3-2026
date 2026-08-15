using System.Collections.Generic;
using System.Linq;

namespace PickandPlace2026.Classes
{
    public class FeederSlot
    {
        public int FeederNumber { get; set; }
        public double PosX { get; set; }
        public double PosY { get; set; }
        public double PosZ { get; set; }
        public string FeederActivationCode { get; set; } = "";
        public bool PickPlusChipHeight { get; set; }
    }

    public class DataComponentFeeders
    {
        public List<FeederSlot> feeders = new();

        public List<FeederSlot> POPFeedersTable()
        {
            feeders = new List<FeederSlot>
            {
                // tape feeders
                new() { FeederNumber = 0, PosX = 30.6, PosY = 8.720, PosZ = 3.2, FeederActivationCode = "M90100", PickPlusChipHeight = false }, // empty
                new() { FeederNumber = 1, PosX = 50.53, PosY = 8.745, PosZ = 3.0, FeederActivationCode = "M90101", PickPlusChipHeight = false }, // dual mosfet
                new() { FeederNumber = 2, PosX = 70.6, PosY = 8.571, PosZ = 3.2, FeederActivationCode = "M90102", PickPlusChipHeight = false }, // 10uf
                new() { FeederNumber = 3, PosX = 90.4, PosY = 8.9, PosZ = 2.3, FeederActivationCode = "M90103", PickPlusChipHeight = false }, // 100nf
                new() { FeederNumber = 4, PosX = 110.35, PosY = 8.83, PosZ = 2.8, FeederActivationCode = "M90104", PickPlusChipHeight = false }, // 10K res
                new() { FeederNumber = 5, PosX = 130.7, PosY = 8.758, PosZ = 2.6, FeederActivationCode = "M90105", PickPlusChipHeight = false }, // 6K8 res
                new() { FeederNumber = 6, PosX = 150.33, PosY = 8.53, PosZ = 2.6, FeederActivationCode = "M90106", PickPlusChipHeight = false }, // 100R
                new() { FeederNumber = 7, PosX = 170.0, PosY = 8.667, PosZ = 3.2, FeederActivationCode = "M90107", PickPlusChipHeight = false }, // 10Kx4
                new() { FeederNumber = 8, PosX = 189.76, PosY = 8.47, PosZ = 3.2, FeederActivationCode = "M90108", PickPlusChipHeight = false }, // 2K2 x 4
                new() { FeederNumber = 9, PosX = 209.75, PosY = 8.049, PosZ = 3.2, FeederActivationCode = "M90109", PickPlusChipHeight = false }, // 1K
                new() { FeederNumber = 10, PosX = 229.63, PosY = 7.975, PosZ = 3.2, FeederActivationCode = "M90110", PickPlusChipHeight = false }, // signal diode
                new() { FeederNumber = 11, PosX = 249.5, PosY = 8.00, PosZ = 3.2, FeederActivationCode = "M90111", PickPlusChipHeight = false }, // 220R x 4
                new() { FeederNumber = 12, PosX = 269.39, PosY = 7.826, PosZ = 3.2, FeederActivationCode = "M90112", PickPlusChipHeight = false },
                new() { FeederNumber = 13, PosX = 289.17, PosY = 7.751, PosZ = 3.2, FeederActivationCode = "M90113", PickPlusChipHeight = false },
                new() { FeederNumber = 14, PosX = 309.17, PosY = 7.677, PosZ = 3.2, FeederActivationCode = "M90114", PickPlusChipHeight = false },
                new() { FeederNumber = 15, PosX = 328.77, PosY = 7.602, PosZ = 3.2, FeederActivationCode = "M90115", PickPlusChipHeight = false },
                // chip feeders
                new() { FeederNumber = 21, PosX = 16.77, PosY = 386.21, PosZ = 7.7, FeederActivationCode = "", PickPlusChipHeight = true },
                new() { FeederNumber = 22, PosX = 44.2, PosY = 385.99, PosZ = 7.7, FeederActivationCode = "", PickPlusChipHeight = true }, // SOIC28
                new() { FeederNumber = 23, PosX = 72.7, PosY = 386.21, PosZ = 8.1, FeederActivationCode = "", PickPlusChipHeight = true }, // SOIC16
                new() { FeederNumber = 24, PosX = 100.75, PosY = 386.21, PosZ = 7.6, FeederActivationCode = "", PickPlusChipHeight = true }, // SOIC14
                new() { FeederNumber = 25, PosX = 128.035, PosY = 385.808, PosZ = 7.9, FeederActivationCode = "", PickPlusChipHeight = true }, // SOIC8
                new() { FeederNumber = 26, PosX = 156.3, PosY = 385.85, PosZ = 8.3, FeederActivationCode = "", PickPlusChipHeight = true }, // SOIC8
            };
            return feeders;
        }

        public void CheckHasRows()
        {
            if (feeders.Count == 0)
            {
                POPFeedersTable();
            }
        }

        private FeederSlot? Find(string fid)
        {
            CheckHasRows();
            if (!int.TryParse(fid, out int feederNumber)) return null;
            return feeders.FirstOrDefault(f => f.FeederNumber == feederNumber);
        }

        public double GetfeederPosX(string fid)
        {
            return Find(fid)?.PosX ?? 0.0;
        }

        public double GetfeederPosY(string fid)
        {
            return Find(fid)?.PosY ?? 0.0;
        }

        public double GetfeederPosZ(string fid)
        {
            return Find(fid)?.PosZ ?? 0.0;
        }

        public string GetfeederActivationCode(string fid)
        {
            return Find(fid)?.FeederActivationCode ?? "";
        }

        public bool GetFeederPickPlusChipHeight(string fid)
        {
            return Find(fid)?.PickPlusChipHeight ?? false;
        }
    }
}
