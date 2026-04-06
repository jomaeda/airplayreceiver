namespace AirPlay.Models.Configs
{
    public sealed class RecordingConfig
    {
        public bool Enabled { get; set; } = true;
        public string OutputPath { get; set; } = "recordings";
        public bool SplitTracks { get; set; } = true;
        public bool SaveArtwork { get; set; } = true;
        public bool WriteMetadataJson { get; set; } = true;
    }
}
