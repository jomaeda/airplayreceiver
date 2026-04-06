using System.Collections.Generic;

namespace AirPlay.Models
{
    public sealed class TrackMetadata
    {
        public string SessionId { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public string AlbumArtist { get; set; }
        public string Album { get; set; }
        public string Genre { get; set; }
        public string Composer { get; set; }
        public ushort? TrackNumber { get; set; }
        public ushort? TrackCount { get; set; }
        public int? DurationMs { get; set; }
        public ulong? PersistentId { get; set; }
        public Dictionary<string, object> Raw { get; set; } = new Dictionary<string, object>();
    }
}
