using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using AirPlay.Models;
using AirPlay.Models.Configs;
using AirPlay.Utils;

namespace AirPlay.Services
{
    public sealed class AudioRecordingService : IDisposable
    {
        private const ushort Channels = 2;
        private const uint SampleRate = 44100;
        private const ushort BitsPerSample = 16;

        private readonly RecordingConfig _config;
        private readonly string _outputRoot;
        private readonly ConcurrentDictionary<string, SessionRecordingState> _sessions = new ConcurrentDictionary<string, SessionRecordingState>();
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public AudioRecordingService(RecordingConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _outputRoot = ResolveOutputRoot(config.OutputPath);
        }

        public void HandlePcmData(PcmData data)
        {
            if (!_config.Enabled || data.Data == null || data.Data.Length == 0)
            {
                return;
            }

            var sessionId = NormalizeSessionId(data.SessionId);
            var state = _sessions.GetOrAdd(sessionId, CreateSessionState);
            var bytesToWrite = Math.Min(data.Length > 0 ? data.Length : data.Data.Length, data.Data.Length);
            if (bytesToWrite <= 0)
            {
                return;
            }

            lock (state.SyncRoot)
            {
                AdvanceTrackIfNeeded(state);
                EnsureTrackWriter(state);

                state.Writer.Write(data.Data, 0, bytesToWrite);
                state.BytesWritten += bytesToWrite;
            }
        }

        public void HandleTrackMetadata(TrackMetadata metadata)
        {
            if (!_config.Enabled || metadata == null)
            {
                return;
            }

            var sessionId = NormalizeSessionId(metadata.SessionId);
            metadata.SessionId = sessionId;
            var state = _sessions.GetOrAdd(sessionId, CreateSessionState);

            lock (state.SyncRoot)
            {
                if (!state.HasOpenTrack)
                {
                    if (state.CurrentMetadata == null || !IsSameTrack(state.CurrentMetadata, metadata))
                    {
                        state.CurrentMetadata = CloneMetadata(metadata);
                        state.CurrentArtwork = null;
                    }
                    else
                    {
                        state.CurrentMetadata = MergeMetadata(state.CurrentMetadata, metadata);
                    }

                    state.NextMetadata = null;
                    state.NextArtwork = null;
                    state.PendingTrackBoundary = false;
                    return;
                }

                if (state.CurrentMetadata == null || IsTrackIdentityEmpty(state.CurrentMetadata) || IsSameTrack(state.CurrentMetadata, metadata))
                {
                    state.CurrentMetadata = MergeMetadata(state.CurrentMetadata, metadata);
                    return;
                }

                state.NextMetadata = MergeMetadata(state.NextMetadata, metadata);
                state.PendingTrackBoundary = _config.SplitTracks;
            }
        }

        public void HandleTrackArtwork(TrackArtwork artwork)
        {
            if (!_config.Enabled || artwork?.Data == null || artwork.Data.Length == 0)
            {
                return;
            }

            var sessionId = NormalizeSessionId(artwork.SessionId);
            artwork.SessionId = sessionId;
            var state = _sessions.GetOrAdd(sessionId, CreateSessionState);

            lock (state.SyncRoot)
            {
                if (state.PendingTrackBoundary && state.HasOpenTrack)
                {
                    state.NextArtwork = CloneArtwork(artwork);
                    return;
                }

                state.CurrentArtwork = CloneArtwork(artwork);
            }
        }

        public void HandleAudioFlush(AudioFlushData flush)
        {
            if (!_config.Enabled || flush == null)
            {
                return;
            }

            var sessionId = NormalizeSessionId(flush.SessionId);
            flush.SessionId = sessionId;
            var state = _sessions.GetOrAdd(sessionId, CreateSessionState);

            lock (state.SyncRoot)
            {
                if (state.PendingTrackBoundary)
                {
                    CompleteCurrentTrack(state);
                    PromotePendingTrack(state);
                }
            }
        }

        public void CompleteAll()
        {
            foreach (var state in _sessions.Values)
            {
                lock (state.SyncRoot)
                {
                    CompleteCurrentTrack(state);
                }
            }
        }

        public void Dispose()
        {
            CompleteAll();
        }

        private static string ResolveOutputRoot(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                configuredPath = "recordings";
            }

            return Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
        }

        private SessionRecordingState CreateSessionState(string sessionId)
        {
            return new SessionRecordingState
            {
                SessionId = sessionId,
                OutputRoot = _outputRoot
            };
        }

        private void AdvanceTrackIfNeeded(SessionRecordingState state)
        {
            if (!state.PendingTrackBoundary)
            {
                return;
            }

            CompleteCurrentTrack(state);
            PromotePendingTrack(state);
        }

        private void EnsureTrackWriter(SessionRecordingState state)
        {
            if (state.Writer != null)
            {
                return;
            }

            Directory.CreateDirectory(_outputRoot);
            Directory.CreateDirectory(state.SessionDirectory);

            state.TrackIndex += 1;
            state.TrackStartedAtUtc = DateTimeOffset.UtcNow;
            state.TempFilePath = Path.Combine(
                state.SessionDirectory,
                $"{state.TrackStartedAtUtc:yyyyMMdd-HHmmssfff}_{state.TrackIndex:D4}.tmp.wav");

            state.Writer = new FileStream(state.TempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var header = Utilities.WriteWavHeader(Channels, SampleRate, BitsPerSample, 0);
            state.Writer.Write(header, 0, header.Length);
            state.BytesWritten = 0;
        }

        private void CompleteCurrentTrack(SessionRecordingState state)
        {
            if (state.Writer == null)
            {
                state.PendingTrackBoundary = false;
                return;
            }

            state.Writer.Position = 0;
            var header = Utilities.WriteWavHeader(Channels, SampleRate, BitsPerSample, (uint)state.BytesWritten);
            state.Writer.Write(header, 0, header.Length);
            state.Writer.Flush();
            state.Writer.Dispose();
            state.Writer = null;

            if (state.BytesWritten <= 0)
            {
                if (!string.IsNullOrWhiteSpace(state.TempFilePath) && File.Exists(state.TempFilePath))
                {
                    File.Delete(state.TempFilePath);
                }

                ResetTrackBuffers(state);
                return;
            }

            var outputBasePath = GetUniqueOutputBasePath(state);
            File.Move(state.TempFilePath, outputBasePath + ".wav");

            if (_config.WriteMetadataJson)
            {
                var manifest = new RecordedTrackManifest
                {
                    SessionId = state.SessionId,
                    StartedAtUtc = state.TrackStartedAtUtc,
                    BytesWritten = state.BytesWritten,
                    Channels = Channels,
                    SampleRate = SampleRate,
                    BitsPerSample = BitsPerSample,
                    Metadata = state.CurrentMetadata,
                    ArtworkContentType = state.CurrentArtwork?.ContentType
                };

                var json = JsonSerializer.Serialize(manifest, _jsonOptions);
                File.WriteAllText(outputBasePath + ".json", json, new UTF8Encoding(false));
            }

            if (_config.SaveArtwork && state.CurrentArtwork?.Data != null && state.CurrentArtwork.Data.Length > 0)
            {
                File.WriteAllBytes(outputBasePath + GetArtworkExtension(state.CurrentArtwork.ContentType), state.CurrentArtwork.Data);
            }

            ResetTrackBuffers(state);
        }

        private static void PromotePendingTrack(SessionRecordingState state)
        {
            if (state.NextMetadata != null)
            {
                state.CurrentMetadata = state.NextMetadata;
                state.NextMetadata = null;
            }
            else
            {
                state.CurrentMetadata = null;
            }

            if (state.NextArtwork != null)
            {
                state.CurrentArtwork = state.NextArtwork;
                state.NextArtwork = null;
            }
            else
            {
                state.CurrentArtwork = null;
            }

            state.PendingTrackBoundary = false;
        }

        private static void ResetTrackBuffers(SessionRecordingState state)
        {
            state.TempFilePath = null;
            state.BytesWritten = 0;
            state.PendingTrackBoundary = false;
        }

        private string GetUniqueOutputBasePath(SessionRecordingState state)
        {
            var baseName = BuildTrackFileName(state);
            var candidate = Path.Combine(state.SessionDirectory, baseName);
            var suffix = 1;

            while (File.Exists(candidate + ".wav") || File.Exists(candidate + ".json"))
            {
                candidate = Path.Combine(state.SessionDirectory, $"{baseName}_{suffix:D2}");
                suffix += 1;
            }

            return candidate;
        }

        private static string BuildTrackFileName(SessionRecordingState state)
        {
            var fragments = new List<string>
            {
                state.TrackStartedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss"),
                state.TrackIndex.ToString("D4")
            };

            var artist = state.CurrentMetadata?.Artist;
            if (string.IsNullOrWhiteSpace(artist))
            {
                artist = state.CurrentMetadata?.AlbumArtist;
            }

            if (!string.IsNullOrWhiteSpace(artist))
            {
                fragments.Add(SanitizePathFragment(artist));
            }

            if (!string.IsNullOrWhiteSpace(state.CurrentMetadata?.Title))
            {
                fragments.Add(SanitizePathFragment(state.CurrentMetadata.Title));
            }
            else
            {
                fragments.Add("track");
            }

            return string.Join("_", fragments.Where(f => !string.IsNullOrWhiteSpace(f)));
        }

        private static string SanitizePathFragment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "unknown";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Trim());
            foreach (var c in invalid)
            {
                builder.Replace(c, '_');
            }

            return string.Join(" ", builder.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static bool IsSameTrack(TrackMetadata left, TrackMetadata right)
        {
            var leftIdentity = GetTrackIdentity(left);
            var rightIdentity = GetTrackIdentity(right);

            if (string.IsNullOrWhiteSpace(leftIdentity) || string.IsNullOrWhiteSpace(rightIdentity))
            {
                return false;
            }

            return string.Equals(leftIdentity, rightIdentity, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTrackIdentityEmpty(TrackMetadata metadata)
        {
            return string.IsNullOrWhiteSpace(GetTrackIdentity(metadata));
        }

        private static string GetTrackIdentity(TrackMetadata metadata)
        {
            if (metadata == null)
            {
                return string.Empty;
            }

            if (metadata.PersistentId.HasValue)
            {
                return $"pid:{metadata.PersistentId.Value}";
            }

            var identityParts = new[]
            {
                metadata.Title?.Trim(),
                metadata.Artist?.Trim(),
                metadata.Album?.Trim(),
                metadata.TrackNumber?.ToString()
            };

            return string.Join("|", identityParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static TrackMetadata MergeMetadata(TrackMetadata current, TrackMetadata incoming)
        {
            if (incoming == null)
            {
                return current;
            }

            var merged = current == null ? new TrackMetadata() : CloneMetadata(current);
            merged.SessionId = incoming.SessionId ?? merged.SessionId;
            merged.Title = Prefer(incoming.Title, merged.Title);
            merged.Artist = Prefer(incoming.Artist, merged.Artist);
            merged.AlbumArtist = Prefer(incoming.AlbumArtist, merged.AlbumArtist);
            merged.Album = Prefer(incoming.Album, merged.Album);
            merged.Genre = Prefer(incoming.Genre, merged.Genre);
            merged.Composer = Prefer(incoming.Composer, merged.Composer);
            merged.TrackNumber = incoming.TrackNumber ?? merged.TrackNumber;
            merged.TrackCount = incoming.TrackCount ?? merged.TrackCount;
            merged.DurationMs = incoming.DurationMs ?? merged.DurationMs;
            merged.PersistentId = incoming.PersistentId ?? merged.PersistentId;

            foreach (var pair in incoming.Raw)
            {
                merged.Raw[pair.Key] = pair.Value;
            }

            return merged;
        }

        private static TrackMetadata CloneMetadata(TrackMetadata metadata)
        {
            return metadata == null
                ? null
                : new TrackMetadata
                {
                    SessionId = metadata.SessionId,
                    Title = metadata.Title,
                    Artist = metadata.Artist,
                    AlbumArtist = metadata.AlbumArtist,
                    Album = metadata.Album,
                    Genre = metadata.Genre,
                    Composer = metadata.Composer,
                    TrackNumber = metadata.TrackNumber,
                    TrackCount = metadata.TrackCount,
                    DurationMs = metadata.DurationMs,
                    PersistentId = metadata.PersistentId,
                    Raw = metadata.Raw.ToDictionary(pair => pair.Key, pair => pair.Value)
                };
        }

        private static TrackArtwork CloneArtwork(TrackArtwork artwork)
        {
            return artwork == null
                ? null
                : new TrackArtwork
                {
                    SessionId = artwork.SessionId,
                    ContentType = artwork.ContentType,
                    Data = artwork.Data.ToArray()
                };
        }

        private static string Prefer(string preferred, string fallback)
        {
            return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
        }

        private static string NormalizeSessionId(string sessionId)
        {
            return string.IsNullOrWhiteSpace(sessionId) ? "default-session" : sessionId;
        }

        private static string GetArtworkExtension(string contentType)
        {
            return contentType?.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/jpg" => ".jpg",
                _ => ".bin"
            };
        }

        private sealed class SessionRecordingState
        {
            public object SyncRoot { get; } = new object();
            public string OutputRoot { get; set; }
            public string SessionId { get; set; }
            public int TrackIndex { get; set; }
            public DateTimeOffset TrackStartedAtUtc { get; set; }
            public string TempFilePath { get; set; }
            public FileStream Writer { get; set; }
            public long BytesWritten { get; set; }
            public TrackMetadata CurrentMetadata { get; set; }
            public TrackMetadata NextMetadata { get; set; }
            public TrackArtwork CurrentArtwork { get; set; }
            public TrackArtwork NextArtwork { get; set; }
            public bool PendingTrackBoundary { get; set; }
            public bool HasOpenTrack => Writer != null;
            public string SessionDirectory => Path.Combine(OutputRoot, SanitizePathFragment(SessionId));
        }

        private sealed class RecordedTrackManifest
        {
            public string SessionId { get; set; }
            public DateTimeOffset StartedAtUtc { get; set; }
            public long BytesWritten { get; set; }
            public ushort Channels { get; set; }
            public uint SampleRate { get; set; }
            public ushort BitsPerSample { get; set; }
            public string ArtworkContentType { get; set; }
            public TrackMetadata Metadata { get; set; }
        }
    }
}
