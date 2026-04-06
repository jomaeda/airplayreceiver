using System;
using System.Threading;
using System.Threading.Tasks;
using AirPlay.Models;

namespace AirPlay
{
    public interface IRtspReceiver
    {
        void OnSetVolume(decimal volume);
        void OnData(H264Data data);
        void OnPCMData(PcmData data);
        void OnTrackMetadata(TrackMetadata metadata);
        void OnTrackArtwork(TrackArtwork artwork);
        void OnAudioFlush(AudioFlushData flush);
    }
}
