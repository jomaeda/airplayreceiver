using System;
using AirPlay.Models.Enums;

namespace AirPlay
{
    public class PCMDecoder : IDecoder
    {
        private int _frameLength;
        private int _channels;
        private int _bitDepth;

        public AudioFormat Type => AudioFormat.PCM;

        public int Config(int sampleRate, int channels, int bitDepth, int frameLength)
        {
            _frameLength = frameLength;
            _channels = channels;
            _bitDepth = bitDepth;
            return 0;
        }

        public int DecodeFrame(byte[] input, ref byte[] output, int length)
        {
            if (output == null || output.Length < input.Length)
            {
                output = new byte[input.Length];
            }

            Array.Copy(input, 0, output, 0, input.Length);
            return 0;
        }

        public int GetOutputStreamLength()
        {
            if (_frameLength <= 0 || _channels <= 0 || _bitDepth <= 0)
            {
                return 0;
            }

            return _frameLength * _channels * (_bitDepth / 8);
        }
    }
}
