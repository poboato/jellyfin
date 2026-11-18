using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Providers.MediaInfo
{
    /// <summary>
    /// Works out where each previously stored stream ended up after a refresh renumbered the streams of an item.
    /// </summary>
    /// <remarks>
    /// Stream indexes are stored by clients and in user data (remembered audio and subtitle selections)
    /// and name the extracted subtitle cache files, so anything keyed on an index has to follow the stream
    /// to its new index, or be dropped when the stream can no longer be identified.
    /// </remarks>
    public static class MediaStreamIndexMap
    {
        /// <summary>
        /// Builds a map from each previous index whose stream moved or disappeared to the index it has now.
        /// </summary>
        /// <param name="previousStreams">The streams stored before the refresh.</param>
        /// <param name="currentStreams">The streams about to be stored.</param>
        /// <returns>
        /// Only the indexes that changed. The value is the new index, or <c>null</c> when the stream is gone
        /// or cannot be told apart from another one. An index absent from the map still names the same stream.
        /// </returns>
        public static IReadOnlyDictionary<int, int?> Build(IReadOnlyList<MediaStream> previousStreams, IReadOnlyList<MediaStream> currentStreams)
        {
            ArgumentNullException.ThrowIfNull(previousStreams);
            ArgumentNullException.ThrowIfNull(currentStreams);

            var current = new Dictionary<StreamKey, MediaStream>();
            foreach (var (key, stream) in Identify(currentStreams))
            {
                current[key] = stream;
            }

            var map = new Dictionary<int, int?>();
            foreach (var (key, stream) in Identify(previousStreams))
            {
                int? newIndex = current.TryGetValue(key, out var match) && IsSameStream(stream, match)
                    ? match.Index
                    : null;

                if (newIndex != stream.Index)
                {
                    map[stream.Index] = newIndex;
                }
            }

            return map;
        }

        // An external stream is identified by its file, its type and its position among streams of that type in
        // that file. An embedded stream is identified by its position inside the media file, which is its index
        // minus the external streams numbered before it; that holds for both external-first and external-last
        // layouts, and when embedded subtitles are filtered out by a library setting.
        private static IEnumerable<(StreamKey Key, MediaStream Stream)> Identify(IReadOnlyList<MediaStream> streams)
        {
            var ordered = streams.Where(s => s.Index >= 0).OrderBy(s => s.Index).ToList();

            var externalsBefore = 0;
            foreach (var stream in ordered)
            {
                if (stream.IsExternal)
                {
                    externalsBefore++;
                }
                else
                {
                    yield return (new StreamKey(false, string.Empty, stream.Type, stream.Index - externalsBefore), stream);
                }
            }

            foreach (var group in ordered.Where(s => s.IsExternal).GroupBy(s => (Path: s.Path ?? string.Empty, s.Type)))
            {
                var ordinal = 0;
                foreach (var stream in group)
                {
                    yield return (new StreamKey(true, group.Key.Path, group.Key.Type, ordinal++), stream);
                }
            }
        }

        // Guards against a replaced file with a different layout: the same slot holding another
        // language or codec is a different track, and pointing at it would be the bug this prevents.
        private static bool IsSameStream(MediaStream previous, MediaStream current)
        {
            return string.Equals(previous.Codec, current.Codec, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.Language, current.Language, StringComparison.OrdinalIgnoreCase);
        }

        private readonly record struct StreamKey(bool IsExternal, string Path, MediaStreamType Type, int Position);
    }
}
