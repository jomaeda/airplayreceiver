namespace AirPlay.Models
{
    public sealed class TrackArtwork
    {
        public string SessionId { get; set; }
        public string ContentType { get; set; }
        public byte[] Data { get; set; }
    }
}
