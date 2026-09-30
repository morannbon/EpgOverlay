using TvAIrPlugin;

namespace EpgOverlay.Parsing;

internal sealed class EpgOverlayDataReader
{
    private readonly AribTextDecoder _decoder;

    public EpgOverlayDataReader()
    {
        _decoder = new AribTextDecoder();
    }

    public LocalEpgDataReadResult Read(string path, CancellationToken cancellationToken = default)
    {
        var result = new LocalEpgDataReadResult { Path = path, ReadAt = DateTimeOffset.Now };
        if (string.IsNullOrWhiteSpace(path))
        {
            result.Message = "EpgDataの場所を指定してください。";
            return result;
        }

        List<string> targets;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets = EnumerateTargetFiles(path).ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Message = $"場所を読み取れませんでした。{ex.Message}";
            return result;
        }

        result.TargetFileCount = targets.Count;
        if (targets.Count == 0)
        {
            result.FileExists = File.Exists(path) || Directory.Exists(path);
            result.Message = result.FileExists ? "対象ファイルが見つかりません。" : "場所が見つかりません。";
            return result;
        }

        var sourceRevisionBeforeRead = BuildSourceRevision(targets);
        if (string.IsNullOrEmpty(sourceRevisionBeforeRead))
        {
            result.FileExists = true;
            result.FailedFileCount = targets.Count;
            result.Message = "EPGデータを読み取れませんでした。";
            return result;
        }

        var serviceKeys = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<LocalEpgOverlayEvent>();
        var failures = 0;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(target);
                if (!file.Exists)
                {
                    failures++;
                    continue;
                }

                result.FileExists = true;
                result.FileSize += file.Length;
                if (!result.FileWriteTime.HasValue || file.LastWriteTime > result.FileWriteTime.Value)
                {
                    result.FileWriteTime = file.LastWriteTime;
                }

                var fileRead = ReadFile(file.FullName, cancellationToken);
                if (!fileRead.Complete || fileRead.Events.Count == 0)
                {
                    failures++;
                    continue;
                }

                result.ReadableFileCount++;
                foreach (var item in fileRead.Events)
                {
                    serviceKeys.Add(ServiceKeyText(item.ServiceKey));
                    events.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failures++;
            }
        }

        result.FailedFileCount = failures;
        result.Events = Deduplicate(events);
        result.ServiceCount = serviceKeys.Count;
        result.EventCount = result.Events.Count;
        result.TitleCount = result.Events.Count(e => !string.IsNullOrWhiteSpace(e.Title));
        result.OutlineCount = result.Events.Count(e => !string.IsNullOrWhiteSpace(e.Outline));
        result.DetailCount = result.Events.Count(e => !string.IsNullOrWhiteSpace(e.Detail));
        result.ExtendedCount = result.Events.Count(e => !string.IsNullOrWhiteSpace(e.Detail) || e.ExtendedItems.Count > 0);
        result.GenreCodeCount = result.Events.Count(e => !string.IsNullOrWhiteSpace(e.GenreCodes));
        result.CandidateCount = result.Events.Count(e => !string.IsNullOrWhiteSpace(e.Title));

        var sourceRevisionAfterRead = BuildSourceRevision(targets);
        var sourceSetStable = !string.IsNullOrEmpty(sourceRevisionAfterRead)
            && string.Equals(sourceRevisionBeforeRead, sourceRevisionAfterRead, StringComparison.Ordinal);
        result.SourceRevision = sourceSetStable ? sourceRevisionAfterRead : string.Empty;
        if (!sourceSetStable) result.FailedFileCount = Math.Max(result.FailedFileCount, 1);

        // The Host projection is replaced as one snapshot, so every selected source must belong
        // to the same stable read and complete successfully before the new snapshot is published.
        result.Ok = result.ReadableFileCount > 0 && result.EventCount > 0 && result.FailedFileCount == 0;
        result.Message = result.Ok
            ? $"読み取りできました。サービス {result.ServiceCount:N0} / 番組 {result.EventCount:N0} / タイトル {result.TitleCount:N0} / 概要 {result.OutlineCount:N0}"
            : $"番組を読み取れませんでした。対象 {result.TargetFileCount:N0} / 失敗 {result.FailedFileCount:N0}";
        return result;
    }


    public string GetSourceRevision(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        try
        {
            return BuildSourceRevision(EnumerateTargetFiles(path).ToList());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildSourceRevision(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) return string.Empty;

        var parts = new List<string>(targets.Count);
        foreach (var target in targets)
        {
            var file = new FileInfo(target);
            if (!file.Exists) return string.Empty;
            parts.Add($"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        }
        return string.Join("\n", parts);
    }

    public IReadOnlyList<TvAirExternalProgramEventDto> CreateExternalEvents(LocalEpgDataReadResult read, string sourceKind, IReadOnlyDictionary<string, string>? serviceNames = null)
    {
        ArgumentNullException.ThrowIfNull(read);

        var results = new List<TvAirExternalProgramEventDto>();
        foreach (var item in read.Events)
        {
            if (string.IsNullOrWhiteSpace(item.Title)) continue;
            var end = item.StartTime + item.Duration;
            if (end <= item.StartTime) continue;

            var serviceKeyText = $"{item.ServiceKey.NetworkId}:{item.ServiceKey.TransportStreamId}:{item.ServiceKey.ServiceId}";
            if (serviceNames is null || !serviceNames.TryGetValue(serviceKeyText, out var serviceName)) continue;

            results.Add(new TvAirExternalProgramEventDto
            {
                SourceKind = string.IsNullOrWhiteSpace(sourceKind) ? "ExternalEpg" : sourceKind,
                SourceEventKey = $"epgdata:{serviceKeyText}:{item.EventId}:{item.StartTime:O}:{end:O}",
                NetworkId = item.ServiceKey.NetworkId,
                TransportStreamId = item.ServiceKey.TransportStreamId,
                ServiceId = item.ServiceKey.ServiceId,
                EventNumber = item.EventId,
                Start = item.StartTime,
                End = end,
                ServiceName = serviceName,
                Title = item.Title,
                Summary = string.IsNullOrWhiteSpace(item.Outline) ? null : item.Outline,
                Detail = string.IsNullOrWhiteSpace(item.Detail) ? null : item.Detail,
                ExtendedItems = item.ExtendedItems.Count == 0 ? null : string.Join("\n", item.ExtendedItems.Select(kv => $"{kv.Key}: {kv.Value}")),
                GenreCodes = string.IsNullOrWhiteSpace(item.GenreCodes) ? null : item.GenreCodes
            });
        }

        return results;
    }

    private static string ServiceKeyText(LocalServiceKey key)
        => $"{key.NetworkId}:{key.TransportStreamId}:{key.ServiceId}";

    private static IReadOnlyList<LocalEpgOverlayEvent> Deduplicate(IEnumerable<LocalEpgOverlayEvent> events)
    {
        return events
            .GroupBy(e => $"{e.ServiceKey.NetworkId}:{e.ServiceKey.TransportStreamId}:{e.ServiceKey.ServiceId}:{e.EventId}:{e.StartTime:O}")
            .Select(g => g
                .OrderByDescending(e => Score(e))
                .ThenByDescending(e => e.SourceWriteTime)
                .ThenBy(e => e.SourcePath, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(e => e.StartTime)
            .ThenBy(e => e.ServiceKey.NetworkId)
            .ThenBy(e => e.ServiceKey.TransportStreamId)
            .ThenBy(e => e.ServiceKey.ServiceId)
            .ToList();
    }

    private static int Score(LocalEpgOverlayEvent item)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(item.Title)) score += 4;
        if (!string.IsNullOrWhiteSpace(item.Outline)) score += 2;
        if (!string.IsNullOrWhiteSpace(item.Detail)) score += 1;
        score += Math.Min(item.ExtendedItems.Count, 4);
        if (!string.IsNullOrWhiteSpace(item.GenreCodes)) score += 1;
        return score;
    }

    private static IEnumerable<string> EnumerateTargetFiles(string path)
    {
        if (File.Exists(path))
        {
            if (IsTargetFile(path)) yield return path;
            yield break;
        }

        if (!Directory.Exists(path)) yield break;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (IsTargetFile(file)) yield return file;
        }
    }

    private static bool IsTargetFile(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[512];
            var read = stream.Read(header);
            if (read >= 8 && System.Text.Encoding.ASCII.GetString(header[..8]) == "EPG-DATA") return true;
            return DetectTsPacketSize(header[..read]) > 0;
        }
        catch
        {
            return false;
        }
    }

    private FileReadResult ReadFile(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var beforeLength = stream.Length;
        var beforeWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        var probeSize = (int)Math.Min(188 * 4096, beforeLength);
        var probe = new byte[Math.Max(0, probeSize)];
        var read = probe.Length > 0 ? stream.Read(probe, 0, probe.Length) : 0;
        stream.Position = 0;

        var sourceWriteTime = File.GetLastWriteTime(path);
        if (read >= 8 && System.Text.Encoding.ASCII.GetString(probe.AsSpan(0, 8)) == "EPG-DATA")
        {
            stream.Position = 0;
            var data = ReadAllBytesShared(stream, cancellationToken);
            var after = new FileInfo(path);
            if (!after.Exists
                || after.Length != beforeLength
                || after.LastWriteTimeUtc != beforeWriteTimeUtc
                || data.LongLength != beforeLength)
            {
                return FileReadResult.Incomplete;
            }

            var parsed = ReadTvTestEpgData(data, path, sourceWriteTime, cancellationToken);
            return parsed.Complete
                ? new FileReadResult(parsed.Events, true)
                : FileReadResult.Incomplete;
        }

        var packetSize = DetectTsPacketSize(probe.AsSpan(0, read));
        if (packetSize == 0) return FileReadResult.Incomplete;

        const long maxReadSize = 0x1000000L;
        if (stream.Length > maxReadSize)
        {
            var offset = ((stream.Length - maxReadSize) / packetSize) * packetSize;
            if (offset > 0) stream.Position = offset;
        }

        var tsEvents = ReadTransportStream(stream, path, packetSize, cancellationToken);
        var afterTs = new FileInfo(path);
        if (!afterTs.Exists
            || afterTs.Length != beforeLength
            || afterTs.LastWriteTimeUtc != beforeWriteTimeUtc)
        {
            return FileReadResult.Incomplete;
        }

        return new FileReadResult(tsEvents, true);
    }

    private static byte[] ReadAllBytesShared(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.Length == 0) return Array.Empty<byte>();
        if (stream.Length > int.MaxValue) throw new IOException("EpgDataが大きすぎます。");

        var buffer = new byte[(int)stream.Length];
        var total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
        }

        if (total == buffer.Length) return buffer;
        Array.Resize(ref buffer, total);
        return buffer;
    }


    private static TvTestEpgDataReadResult ReadTvTestEpgData(byte[] data, string path, DateTimeOffset sourceWriteTime, CancellationToken cancellationToken)
    {
        // TVTest/LibISDB EPG-DATA is not a flat event record stream.
        // It is a chunked file:
        //   FileHeader(24 bytes)
        //   Service chunk(0x02, 8 bytes: NID/TSID/SID/EventCount)
        //     Event chunk(0x04, 24 bytes: event id, flags, start, duration, updated)
        //       EventName/EventText/EventExtendedText/EventGroup chunks
        //       EventGroup(COMMON) may point to another service/event whose descriptors are shared.
        //     EventEnd(0x05)
        //   ServiceEnd(0x03)
        //   End(0x01)
        // Service identity is defined by the enclosing Service chunk and inherited by its events.
        return ReadTvTestEpgDataChunked(data, path, sourceWriteTime, cancellationToken);
    }

    private static TvTestEpgDataReadResult ReadTvTestEpgDataChunked(byte[] data, string path, DateTimeOffset sourceWriteTime, CancellationToken cancellationToken)
    {
        var results = new List<LocalEpgOverlayEvent>();
        if (data.Length < 24) return new TvTestEpgDataReadResult(results, false);
        if (!System.Text.Encoding.ASCII.GetString(data, 0, Math.Min(8, data.Length)).Equals("EPG-DATA", StringComparison.Ordinal))
            return new TvTestEpgDataReadResult(results, false);

        var version = ReadUInt32Little(data, 8);
        if (version > 0) return new TvTestEpgDataReadResult(results, false);

        var complete = false;
        var pos = 24;
        while (TryReadChunk(data, ref pos, out var tag, out var payloadStart, out var payloadEnd))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tag == 0x01)
            {
                complete = true;
                pos = payloadEnd;
                break; // File End
            }
            if (tag != 0x02 || payloadEnd - payloadStart != 8)
            {
                pos = payloadEnd;
                continue;
            }

            var serviceKey = new LocalServiceKey
            {
                NetworkId = ReadUInt16Little(data, payloadStart),
                TransportStreamId = ReadUInt16Little(data, payloadStart + 2),
                ServiceId = ReadUInt16Little(data, payloadStart + 4)
            };
            var declaredEventCount = ReadUInt16Little(data, payloadStart + 6);
            pos = payloadEnd;

            if (!IsPlausibleServiceKey(serviceKey) || declaredEventCount == 0)
            {
                SkipToServiceEnd(data, ref pos);
                continue;
            }

            while (TryReadChunk(data, ref pos, out var childTag, out var childPayloadStart, out var childPayloadEnd))
            {
                if (childTag == 0x03) break; // ServiceEnd

                if (childTag != 0x04 || childPayloadEnd - childPayloadStart != 24)
                {
                    pos = childPayloadEnd;
                    continue;
                }

                var eventId = ReadUInt16Little(data, childPayloadStart);
                DateTimeOffset startTime;
                try
                {
                    startTime = DecodeTvTestEpgDateTime(data, childPayloadStart + 4);
                }
                catch
                {
                    pos = childPayloadEnd;
                    SkipToEventEnd(data, ref pos);
                    continue;
                }
                var duration = TimeSpan.FromSeconds(ReadUInt32Little(data, childPayloadStart + 12));
                pos = childPayloadEnd;

                var title = string.Empty;
                var outline = string.Empty;
                var detailParts = new List<string>();
                var extendedItems = new Dictionary<string, string>();
                var genreCodes = new List<string>();
                ushort? commonServiceId = null;
                ushort? commonEventId = null;

                while (TryReadChunk(data, ref pos, out var eventTag, out var eventPayloadStart, out var eventPayloadEnd))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (eventTag == 0x05) break; // EventEnd

                    switch (eventTag)
                    {
                        case 0x09: // EventName
                            title = ReadTvTestChunkString(data, eventPayloadStart, eventPayloadEnd);
                            break;
                        case 0x0A: // EventText
                            outline = ReadTvTestChunkString(data, eventPayloadStart, eventPayloadEnd);
                            break;
                        case 0x0B: // EventExtendedText
                            ReadTvTestExtendedText(data, eventPayloadStart, eventPayloadEnd, detailParts, extendedItems);
                            break;
                        case 0x0C: // EventGroup
                            ReadTvTestCommonEventReference(
                                data, eventPayloadStart, eventPayloadEnd, serviceKey.ServiceId,
                                ref commonServiceId, ref commonEventId);
                            break;
                        default:
                            AddGenreCodes(genreCodes, ParseGenreCodesFromChunkPayload(eventTag, data.AsSpan(eventPayloadStart, eventPayloadEnd - eventPayloadStart)));
                            break;
                    }

                    pos = eventPayloadEnd;
                }

                if (!IsPlausibleEvent(startTime, duration)) continue;
                if (string.IsNullOrWhiteSpace(title)
                    && string.IsNullOrWhiteSpace(outline)
                    && detailParts.Count == 0
                    && extendedItems.Count == 0
                    && (!commonServiceId.HasValue || !commonEventId.HasValue)) continue;

                var detail = string.Join(" ", detailParts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
                results.Add(new LocalEpgOverlayEvent
                {
                    ServiceKey = new LocalServiceKey
                    {
                        NetworkId = serviceKey.NetworkId,
                        TransportStreamId = serviceKey.TransportStreamId,
                        ServiceId = serviceKey.ServiceId
                    },
                    EventId = eventId,
                    StartTime = startTime,
                    Duration = duration,
                    Title = title,
                    Outline = outline,
                    Detail = detail,
                    ExtendedItems = extendedItems,
                    GenreCodes = FormatGenreCodes(genreCodes),
                    CommonServiceId = commonServiceId,
                    CommonEventId = commonEventId,
                    SourcePath = path,
                    SourceWriteTime = sourceWriteTime
                });
            }
        }

        ResolveTvTestCommonEvents(results);
        return new TvTestEpgDataReadResult(results, complete);
    }

    private static void ReadTvTestCommonEventReference(
        byte[] data, int start, int end, int currentServiceId,
        ref ushort? commonServiceId, ref ushort? commonEventId)
    {
        if (start >= end) return;

        var pos = start;
        var groupCount = data[pos++];
        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            if (pos + 2 > end) return;
            var groupType = data[pos++];
            var eventCount = data[pos++];
            var eventBytes = eventCount * 8;
            if (pos + eventBytes > end) return;

            // ARIB event_group_descriptor group_type=0x01 is event sharing (common event).
            // TVTest/LibISDB resolves the single referenced service/event within the same NID/TSID.
            if (groupType == 0x01 && eventCount == 1)
            {
                var referencedServiceId = ReadUInt16Little(data, pos);
                var referencedEventId = ReadUInt16Little(data, pos + 2);
                if (referencedServiceId != currentServiceId)
                {
                    commonServiceId = referencedServiceId;
                    commonEventId = referencedEventId;
                }
            }

            pos += eventBytes;
        }
    }

    private static void ResolveTvTestCommonEvents(List<LocalEpgOverlayEvent> events)
    {
        var byIdentity = events
            .GroupBy(e => (
                e.ServiceKey.NetworkId,
                e.ServiceKey.TransportStreamId,
                e.ServiceKey.ServiceId,
                e.EventId))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(Score).First());

        var resolved = new HashSet<(int NetworkId, int TransportStreamId, int ServiceId, ushort EventId)>();
        var resolving = new HashSet<(int NetworkId, int TransportStreamId, int ServiceId, ushort EventId)>();

        foreach (var item in events)
        {
            ResolveTvTestCommonEvent(item, byIdentity, resolved, resolving);
        }
    }

    private static void ResolveTvTestCommonEvent(
        LocalEpgOverlayEvent item,
        IReadOnlyDictionary<(int NetworkId, int TransportStreamId, int ServiceId, ushort EventId), LocalEpgOverlayEvent> byIdentity,
        HashSet<(int NetworkId, int TransportStreamId, int ServiceId, ushort EventId)> resolved,
        HashSet<(int NetworkId, int TransportStreamId, int ServiceId, ushort EventId)> resolving)
    {
        var itemKey = (
            item.ServiceKey.NetworkId,
            item.ServiceKey.TransportStreamId,
            item.ServiceKey.ServiceId,
            item.EventId);
        if (resolved.Contains(itemKey)) return;
        if (!resolving.Add(itemKey)) return;

        try
        {
            if (!item.CommonServiceId.HasValue || !item.CommonEventId.HasValue) return;

            var sourceKey = (
                item.ServiceKey.NetworkId,
                item.ServiceKey.TransportStreamId,
                (int)item.CommonServiceId.Value,
                item.CommonEventId.Value);
            if (!byIdentity.TryGetValue(sourceKey, out var source)) return;

            ResolveTvTestCommonEvent(source, byIdentity, resolved, resolving);

            if (string.IsNullOrWhiteSpace(item.Title)) item.Title = source.Title;
            if (string.IsNullOrWhiteSpace(item.Outline)) item.Outline = source.Outline;
            if (string.IsNullOrWhiteSpace(item.Detail)) item.Detail = source.Detail;
            if (item.ExtendedItems.Count == 0 && source.ExtendedItems.Count > 0)
                item.ExtendedItems = new Dictionary<string, string>(source.ExtendedItems);
            if (string.IsNullOrWhiteSpace(item.GenreCodes)) item.GenreCodes = source.GenreCodes;
        }
        finally
        {
            resolving.Remove(itemKey);
            resolved.Add(itemKey);
        }
    }


    private static ushort ReadUInt16Little(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length) return 0;
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static uint ReadUInt32Little(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) return 0;
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    private static bool TryReadChunk(byte[] data, ref int pos, out byte tag, out int payloadStart, out int payloadEnd)
    {
        tag = 0;
        payloadStart = 0;
        payloadEnd = 0;
        if (pos < 0 || pos + 3 > data.Length) return false;

        tag = data[pos];
        var size = ReadUInt16Little(data, pos + 1);
        payloadStart = pos + 3;
        payloadEnd = payloadStart + size;
        if (payloadEnd < payloadStart || payloadEnd > data.Length) return false;

        pos = payloadStart;
        return true;
    }

    private static void SkipToServiceEnd(byte[] data, ref int pos)
    {
        while (TryReadChunk(data, ref pos, out var tag, out var payloadStart, out var payloadEnd))
        {
            pos = payloadEnd;
            if (tag == 0x03 || tag == 0x01) return;
        }
    }


    private static void SkipToEventEnd(byte[] data, ref int pos)
    {
        while (TryReadChunk(data, ref pos, out var tag, out var payloadStart, out var payloadEnd))
        {
            pos = payloadEnd;
            if (tag == 0x05 || tag == 0x03 || tag == 0x01) return;
        }
    }

    private static DateTimeOffset DecodeTvTestEpgDateTime(byte[] data, int offset)
    {
        var year = ReadUInt16Little(data, offset);
        var month = data[offset + 2];
        var day = data[offset + 4];
        var hour = data[offset + 5];
        var minute = data[offset + 6];
        var second = data[offset + 7];
        return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromHours(9));
    }

    private static string ReadTvTestChunkString(byte[] data, int start, int end)
    {
        if (start + 2 > end) return string.Empty;
        var charCount = ReadUInt16Little(data, start);
        var byteCount = charCount * 2;
        if (charCount == 0 || charCount > 4096 || start + 2 + byteCount > end) return string.Empty;
        return System.Text.Encoding.Unicode.GetString(data, start + 2, byteCount).Trim();
    }

    private static void ReadTvTestExtendedText(byte[] data, int start, int end, List<string> detailParts, Dictionary<string, string> extendedItems)
    {
        if (start >= end) return;
        var pos = start;
        var count = data[pos++];
        for (var i = 0; i < count && pos < end; i++)
        {
            var name = ReadTvTestChunkStringAt(data, ref pos, end);
            var text = ReadTvTestChunkStringAt(data, ref pos, end);
            if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(text))
            {
                var key = string.IsNullOrWhiteSpace(name) ? "詳細" : name;
                AddOrAppendExtendedItem(extendedItems, key, text);
                if (!string.IsNullOrWhiteSpace(text)) detailParts.Add(text);
            }
        }
    }

    private static void AddOrAppendExtendedItem(Dictionary<string, string> items, string key, string text)
    {
        if (!items.TryGetValue(key, out var existing))
        {
            items[key] = text;
            return;
        }

        if (string.IsNullOrWhiteSpace(text) || string.Equals(existing, text, StringComparison.Ordinal)) return;
        if (string.IsNullOrWhiteSpace(existing))
        {
            items[key] = text;
            return;
        }

        items[key] = existing + "\n" + text;
    }

    private static string ReadTvTestChunkStringAt(byte[] data, ref int pos, int end)
    {
        if (pos + 2 > end) return string.Empty;
        var charCount = ReadUInt16Little(data, pos);
        pos += 2;
        var byteCount = charCount * 2;
        if (charCount == 0)
        {
            return string.Empty;
        }
        if (charCount > 4096 || pos + byteCount > end)
        {
            pos = end;
            return string.Empty;
        }

        var text = System.Text.Encoding.Unicode.GetString(data, pos, byteCount).Trim();
        pos += byteCount;
        return text;
    }

    private static bool IsPlausibleServiceKey(LocalServiceKey key)
    {
        if (key.NetworkId <= 0 || key.TransportStreamId <= 0 || key.ServiceId <= 0) return false;
        // These are intentionally broad ARIB/DVB guards. Do not hard-code the user's channel set here.
        if (key.NetworkId > 0xffff || key.TransportStreamId > 0xffff || key.ServiceId > 0xffff) return false;
        return true;
    }

    private static bool IsPlausibleEvent(DateTimeOffset start, TimeSpan duration)
    {
        if (start.Year < 2020 || start.Year > 2100) return false;
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24)) return false;
        return true;
    }

    private List<LocalEpgOverlayEvent> ReadTransportStream(Stream stream, string path, int packetSize, CancellationToken cancellationToken)
    {
        var results = new List<LocalEpgOverlayEvent>();
        var packet = new byte[packetSize];
        var sectionBuffers = new Dictionary<int, List<byte>>
        {
            [0x12] = new List<byte>(4096),
            [0x27] = new List<byte>(4096)
        };
        var sourceWriteTime = File.GetLastWriteTime(path);

        while (ReadExact(stream, packet, packetSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = FindSyncOffset(packet);
            if (offset < 0 || offset + 188 > packet.Length) continue;
            var ts = packet.AsSpan(offset, 188);
            if (ts[0] != 0x47) continue;

            var payloadStart = (ts[1] & 0x40) != 0;
            var pid = ((ts[1] & 0x1f) << 8) | ts[2];
            if (pid != 0x12 && pid != 0x27) continue;

            var adaptation = (ts[3] >> 4) & 0x03;
            if (adaptation is 0 or 2) continue;

            var payloadOffset = 4;
            if (adaptation == 3)
            {
                if (payloadOffset >= 188) continue;
                var adaptationLength = ts[payloadOffset];
                payloadOffset += 1 + adaptationLength;
            }
            if (payloadOffset >= 188) continue;

            var buffer = sectionBuffers[pid];
            var payload = ts[payloadOffset..];
            if (payloadStart)
            {
                if (payload.Length == 0) continue;
                var pointer = payload[0];
                var pointerEnd = 1 + pointer;
                if (pointerEnd > payload.Length)
                {
                    buffer.Clear();
                    continue;
                }

                if (pointer > 0 && buffer.Count > 0)
                {
                    buffer.AddRange(payload[1..pointerEnd].ToArray());
                    results.AddRange(DrainSections(buffer, path, sourceWriteTime));
                }
                else if (buffer.Count > 0)
                {
                    buffer.Clear();
                }

                if (pointerEnd >= payload.Length) continue;
                buffer.AddRange(payload[pointerEnd..].ToArray());
            }
            else
            {
                if (buffer.Count == 0) continue;
                buffer.AddRange(payload.ToArray());
            }

            results.AddRange(DrainSections(buffer, path, sourceWriteTime));
        }

        return results;
    }

    private List<LocalEpgOverlayEvent> DrainSections(List<byte> buffer, string path, DateTimeOffset sourceWriteTime)
    {
        var results = new List<LocalEpgOverlayEvent>();
        while (buffer.Count >= 3)
        {
            if (buffer[0] == 0xff)
            {
                buffer.RemoveAt(0);
                continue;
            }

            var sectionLength = ((buffer[1] & 0x0f) << 8) | buffer[2];
            var total = 3 + sectionLength;
            if (sectionLength <= 0 || total > 4096)
            {
                buffer.RemoveAt(0);
                continue;
            }
            if (buffer.Count < total) break;

            var section = buffer.GetRange(0, total).ToArray();
            buffer.RemoveRange(0, total);
            results.AddRange(ParseEitSection(section, path, sourceWriteTime));
        }

        return results;
    }

    private List<LocalEpgOverlayEvent> ParseEitSection(byte[] sectionBytes, string path, DateTimeOffset sourceWriteTime)
    {
        var results = new List<LocalEpgOverlayEvent>();
        ReadOnlySpan<byte> section = sectionBytes;
        if (section.Length < 18) return results;
        var tableId = section[0];
        if (tableId < 0x4e || tableId > 0x6f) return results;

        var sectionLength = ((section[1] & 0x0f) << 8) | section[2];
        var total = 3 + sectionLength;
        if (total > section.Length || total < 18) return results;

        var serviceId = ReadUInt16(section, 3);
        var transportStreamId = ReadUInt16(section, 8);
        var networkId = ReadUInt16(section, 10);
        var pos = 14;
        var end = total - 4;

        while (pos + 12 <= end)
        {
            var eventId = ReadUInt16(section, pos);
            var start = DecodeMjdBcd(section.Slice(pos + 2, 5));
            var duration = DecodeDuration(section.Slice(pos + 7, 3));
            var descriptorLoopLength = ((section[pos + 10] & 0x0f) << 8) | section[pos + 11];
            pos += 12;
            if (pos + descriptorLoopLength > end) break;

            var title = string.Empty;
            var outline = string.Empty;
            var detailParts = new List<string>();
            var items = new Dictionary<string, string>();
            var genreCodes = new List<string>();
            var descriptorEnd = pos + descriptorLoopLength;

            while (pos + 2 <= descriptorEnd)
            {
                var tag = section[pos];
                var length = section[pos + 1];
                pos += 2;
                if (pos + length > descriptorEnd) break;
                var desc = section.Slice(pos, length);
                pos += length;

                if (tag == 0x4d)
                {
                    ParseShortEventDescriptor(desc, ref title, ref outline);
                }
                else if (tag == 0x4e)
                {
                    ParseExtendedEventDescriptor(desc, detailParts, items);
                }
                else if (tag == 0x54)
                {
                    AddGenreCodes(genreCodes, ParseContentDescriptor(desc));
                }
            }

            pos = descriptorEnd;
            var detail = string.Join(" ", detailParts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct());
            results.Add(new LocalEpgOverlayEvent
            {
                ServiceKey = new LocalServiceKey { NetworkId = networkId, TransportStreamId = transportStreamId, ServiceId = serviceId },
                EventId = eventId,
                StartTime = start,
                Duration = duration,
                Title = title,
                Outline = outline,
                Detail = detail,
                ExtendedItems = items,
                GenreCodes = FormatGenreCodes(genreCodes),
                SourcePath = path,
                SourceWriteTime = sourceWriteTime
            });
        }

        return results;
    }

    private void ParseShortEventDescriptor(ReadOnlySpan<byte> desc, ref string title, ref string outline)
    {
        if (desc.Length < 5) return;
        var pos = 3;
        var nameLength = desc[pos++];
        if (pos + nameLength > desc.Length) return;
        var name = _decoder.Decode(desc.Slice(pos, nameLength));
        pos += nameLength;
        if (pos >= desc.Length) return;
        var textLength = desc[pos++];
        if (pos + textLength > desc.Length) return;
        var text = _decoder.Decode(desc.Slice(pos, textLength));

        if (!string.IsNullOrWhiteSpace(name)) title = name;
        if (!string.IsNullOrWhiteSpace(text)) outline = text;
    }

    private void ParseExtendedEventDescriptor(ReadOnlySpan<byte> desc, List<string> detailParts, Dictionary<string, string> items)
    {
        if (desc.Length < 6) return;
        var pos = 4;
        var itemsLength = desc[pos++];
        var itemsEnd = Math.Min(desc.Length, pos + itemsLength);
        while (pos + 1 <= itemsEnd && pos < itemsEnd)
        {
            var itemNameLength = desc[pos++];
            if (pos + itemNameLength > itemsEnd) break;
            var itemName = _decoder.Decode(desc.Slice(pos, itemNameLength));
            pos += itemNameLength;
            if (pos >= itemsEnd) break;
            var itemTextLength = desc[pos++];
            if (pos + itemTextLength > itemsEnd) break;
            var itemText = _decoder.Decode(desc.Slice(pos, itemTextLength));
            pos += itemTextLength;
            if (!string.IsNullOrWhiteSpace(itemName) || !string.IsNullOrWhiteSpace(itemText))
            {
                var key = string.IsNullOrWhiteSpace(itemName) ? "詳細" : itemName;
                AddOrAppendExtendedItem(items, key, itemText);
            }
        }

        pos = itemsEnd;
        if (pos >= desc.Length) return;
        var textLength = desc[pos++];
        if (pos + textLength > desc.Length) return;
        var text = _decoder.Decode(desc.Slice(pos, textLength));
        if (!string.IsNullOrWhiteSpace(text)) detailParts.Add(text);
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    private static int DetectTsPacketSize(ReadOnlySpan<byte> data)
    {
        foreach (var size in new[] { 188, 192, 204 })
        {
            for (var offset = 0; offset < Math.Min(size, data.Length); offset++)
            {
                if (offset + size * 2 >= data.Length) continue;
                if (data[offset] == 0x47 && data[offset + size] == 0x47 && data[offset + size * 2] == 0x47)
                {
                    return size;
                }
            }
        }
        return data.Length >= 1 && data[0] == 0x47 ? 188 : 0;
    }

    private static int FindSyncOffset(ReadOnlySpan<byte> packet)
    {
        if (packet.Length >= 188 && packet[0] == 0x47) return 0;
        for (var i = 0; i <= packet.Length - 188; i++)
        {
            if (packet[i] == 0x47) return i;
        }
        return -1;
    }


    private static IReadOnlyList<string> ParseGenreCodesFromChunkPayload(byte chunkTag, ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0) return Array.Empty<string>();

        var fromDescriptorLoop = ParseDescriptorLoopGenreCodes(payload);
        if (fromDescriptorLoop.Count > 0) return fromDescriptorLoop;

        if (!IsTvTestContentChunkTag(chunkTag)) return Array.Empty<string>();

        var fromContentPayload = ParseContentDescriptor(payload);
        if (fromContentPayload.Count > 0) return fromContentPayload;

        if (payload.Length >= 3)
        {
            var count8 = payload[0];
            if (count8 > 0 && count8 <= 16 && 1 + count8 * 2 <= payload.Length)
            {
                var fromCount8 = ParseContentDescriptor(payload.Slice(1, count8 * 2));
                if (fromCount8.Count > 0) return fromCount8;
            }
        }

        if (payload.Length >= 4)
        {
            var count16 = ReadUInt16Little(payload, 0);
            if (count16 > 0 && count16 <= 16 && 2 + count16 * 2 <= payload.Length)
            {
                var fromCount16 = ParseContentDescriptor(payload.Slice(2, count16 * 2));
                if (fromCount16.Count > 0) return fromCount16;
            }
        }

        return Array.Empty<string>();
    }

    private static bool IsTvTestContentChunkTag(byte chunkTag)
    {
        // Verified against an actual TVTest EPG-DATA sample. Event content information is tag 0x08
        // with an 8-bit entry count followed by count pairs of content_nibble/user_nibble bytes.
        // Tag 0x0C is a different event-information block and must not be interpreted as genre data.
        return chunkTag == 0x08;
    }

    private static IReadOnlyList<string> ParseDescriptorLoopGenreCodes(ReadOnlySpan<byte> payload)
    {
        var results = new List<string>();
        var pos = 0;
        while (pos + 2 <= payload.Length)
        {
            var tag = payload[pos];
            var length = payload[pos + 1];
            pos += 2;
            if (pos + length > payload.Length) return Array.Empty<string>();

            if (tag == 0x54)
            {
                AddGenreCodes(results, ParseContentDescriptor(payload.Slice(pos, length)));
            }

            pos += length;
        }

        return pos == payload.Length ? results : Array.Empty<string>();
    }

    private static IReadOnlyList<string> ParseContentDescriptor(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || (payload.Length % 2) != 0) return Array.Empty<string>();

        var results = new List<string>();
        for (var i = 0; i + 1 < payload.Length; i += 2)
        {
            var code = payload[i];
            if (!IsPlausibleContentNibble(code)) continue;
            results.Add(code.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return results;
    }

    private static bool IsPlausibleContentNibble(byte code)
    {
        var level1 = (code >> 4) & 0x0f;
        // ARIB content_nibble_level_1 uses defined broadcast genre classes. 0xF is user-defined.
        return level1 <= 0x0b || level1 == 0x0f;
    }

    private static void AddGenreCodes(List<string> target, IEnumerable<string> source)
    {
        foreach (var code in source)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            if (target.Contains(code, StringComparer.OrdinalIgnoreCase)) continue;
            target.Add(code.ToUpperInvariant());
        }
    }

    private static string FormatGenreCodes(IReadOnlyCollection<string> genreCodes)
    {
        if (genreCodes.Count == 0) return string.Empty;
        return string.Join(",", genreCodes);
    }

    private static ushort ReadUInt16Little(ReadOnlySpan<byte> data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length) return 0;
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static TimeSpan DecodeDuration(ReadOnlySpan<byte> bcd)
    {
        return new TimeSpan(DecodeBcd(bcd[0]), DecodeBcd(bcd[1]), DecodeBcd(bcd[2]));
    }

    private static DateTimeOffset DecodeMjdBcd(ReadOnlySpan<byte> bytes)
    {
        var mjd = (bytes[0] << 8) | bytes[1];
        var date = MjdToDate(mjd);
        return new DateTimeOffset(date.Year, date.Month, date.Day, DecodeBcd(bytes[2]), DecodeBcd(bytes[3]), DecodeBcd(bytes[4]), TimeSpan.FromHours(9));
    }

    private static DateTime MjdToDate(int mjd)
    {
        var j = mjd + 2400001 + 68569;
        var c = 4 * j / 146097;
        j -= (146097 * c + 3) / 4;
        var y = 4000 * (j + 1) / 1461001;
        j = j - 1461 * y / 4 + 31;
        var m = 80 * j / 2447;
        var d = j - 2447 * m / 80;
        j = m / 11;
        m = m + 2 - 12 * j;
        y = 100 * (c - 49) + y + j;
        return new DateTime(y, m, d);
    }

    private static int DecodeBcd(byte value) => ((value >> 4) * 10) + (value & 0x0f);
}

internal readonly record struct FileReadResult(IReadOnlyList<LocalEpgOverlayEvent> Events, bool Complete)
{
    public static FileReadResult Incomplete { get; } = new(Array.Empty<LocalEpgOverlayEvent>(), false);
}

internal readonly record struct TvTestEpgDataReadResult(IReadOnlyList<LocalEpgOverlayEvent> Events, bool Complete);

internal sealed class LocalServiceKey
{
    public int NetworkId { get; set; }
    public int TransportStreamId { get; set; }
    public int ServiceId { get; set; }
}

internal sealed class TvTestEpgDataHeader
{
    public int Offset { get; set; }
    public ushort NetworkId { get; set; }
    public ushort TransportStreamId { get; set; }
    public ushort ServiceId { get; set; }
    public ushort EventId { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public TimeSpan Duration { get; set; }
}

internal sealed class LocalEpgDataReadResult
{
    public bool Ok { get; set; }
    public bool FileExists { get; set; }
    public string Path { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTimeOffset? FileWriteTime { get; set; }
    public int TargetFileCount { get; set; }
    public int ReadableFileCount { get; set; }
    public int FailedFileCount { get; set; }
    public int ServiceCount { get; set; }
    public int EventCount { get; set; }
    public int TitleCount { get; set; }
    public int OutlineCount { get; set; }
    public int DetailCount { get; set; }
    public int ExtendedCount { get; set; }
    public int GenreCodeCount { get; set; }
    public int CandidateCount { get; set; }
    public DateTimeOffset ReadAt { get; set; }
    public string Message { get; set; } = string.Empty;
    public string SourceRevision { get; set; } = string.Empty;
    public IReadOnlyList<LocalEpgOverlayEvent> Events { get; set; } = Array.Empty<LocalEpgOverlayEvent>();
}

internal sealed class LocalEpgOverlayEvent
{
    public LocalServiceKey ServiceKey { get; set; } = new();
    public ushort EventId { get; set; }
    public DateTimeOffset StartTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Outline { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> ExtendedItems { get; set; } = new Dictionary<string, string>();
    public string GenreCodes { get; set; } = string.Empty;
    public ushort? CommonServiceId { get; set; }
    public ushort? CommonEventId { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public DateTimeOffset SourceWriteTime { get; set; }
}
