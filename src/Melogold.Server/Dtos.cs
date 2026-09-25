using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Melogold.Server;

// Схемы контракта API §4 (имена совпадают с компонентами OpenAPI). Клиент игнорирует неизвестные поля
// и переживает неизвестные значения перечислений (API §1.3): перечисления — строки.

public sealed record ArtistRefDto(string? Id, string Name);

public sealed record TrackDto(
    string VideoId,
    string Title,
    string? ArtistsText,
    IReadOnlyList<ArtistRefDto>? Artists,
    string? AlbumId,
    string? AlbumTitle,
    long? DurationMs,
    string? DurationText,
    string? ThumbnailUrl,
    bool Explicit,
    string? VideoType,
    bool MetadataStub);

/// <summary>Метаданные трека в op (<c>TrackInput</c>): сервер разбирает мягко, пустой title шлётся как отсутствие.</summary>
public sealed record TrackInput
{
    public required string VideoId { get; init; }
    public string? Title { get; init; }
    public string? ArtistsText { get; init; }
    public IReadOnlyList<ArtistRefDto>? Artists { get; init; }
    public string? AlbumId { get; init; }
    public string? AlbumTitle { get; init; }
    public long? DurationMs { get; init; }
    public string? DurationText { get; init; }
    public string? ThumbnailUrl { get; init; }
    public bool? Explicit { get; init; }
    public string? VideoType { get; init; }
}

public sealed record DeviceInput(string Hwid, string Name, string Platform, string? OsVersion, string? Model, string? ClientVersion);

public sealed record DevicePatch(string Hwid, string? Name = null, string? OsVersion = null, string? Model = null, string? ClientVersion = null);

public sealed record DeviceDto(
    string Id,
    string Name,
    string ReportedName,
    string? CustomName,
    string Platform,
    string? OsVersion,
    string? Model,
    string? ClientVersion,
    string LinkedVia,
    string? LinkedByDeviceId,
    string CreatedAt,
    string LastSeenAt,
    string? LastSyncAt,
    string? RecentUntil,
    bool IsCurrent);

public sealed record RecoveryCodeStatus(string CreatedAt, bool Confirmed);

public sealed record UserDto(string Id, string Login, string CreatedAt, string PasswordChangedAt, RecoveryCodeStatus? RecoveryCodeStatus);

public sealed record TokenPair(string AccessToken, string AccessTokenExpiresAt, string RefreshToken, string RefreshTokenExpiresAt);

public sealed record AuthSession(
    UserDto User,
    DeviceDto Device,
    TokenPair Tokens,
    string ServerId,
    string ServerTime,
    string? RecoveryCode,
    int SignedOutDevices);

public sealed record RefreshResponse(TokenPair Tokens, DeviceDto Device, string ServerId, string ServerTime);

public sealed record MeResponse(UserDto User, DeviceDto Device, string ServerId, string ServerTime);

public sealed record DeviceListResponse(IReadOnlyList<DeviceDto> Devices, int? MaxDevices);

public sealed record RevokeOthersResponse(int RevokedCount);

public sealed record RegisterChallenge(string Challenge, int Bits, string ExpiresAt);

public sealed record PowSolution(string Challenge, string Nonce);

public sealed record RecoveryCodeResponse(string RecoveryCode, string CreatedAt);

public sealed record ChangePasswordResponse(UserDto User, TokenPair Tokens, int SignedOutDevices);

// ---------- /server/info (API §4.2) ----------

public sealed record SyncFeature(int Protocol, int MinProtocol, IReadOnlyList<string> Kinds, IReadOnlyList<string> Streams);

public sealed record FeatureVersion(int Version);

public sealed record DeviceLinkingFeature(int Version, IReadOnlyList<string> Modes, int TtlSeconds, int LongPollSeconds);

public sealed record ServerFeatures(
    SyncFeature? Sync,
    FeatureVersion? Playback,
    DeviceLinkingFeature? DeviceLinking,
    FeatureVersion? RecoveryCode,
    FeatureVersion? Export,
    FeatureVersion? AccountDeletion,
    FeatureVersion? RegistrationPow,
    FeatureVersion? Lyrics = null);

public sealed record ServerLinks(string? Source, string? Privacy, string? Contact);

public sealed record ServerInfo(
    string Software,
    string Version,
    string? Revision,
    int ApiVersion,
    int MinApiVersion,
    string ServerId,
    string InstanceName,
    string? PublicUrl,
    bool SecureTransport,
    string Registration,
    ServerFeatures? Features,
    JsonObject? Limits,
    ServerLinks? Links,
    string ServerTime);

// ---------- Синхронизация (API §4.7–§4.8) ----------

public sealed record MergePlanInput(string LocalKey, string Name, string? SyncId = null, string? BrowseId = null);

public sealed record MergePlanRequest(IReadOnlyList<MergePlanInput> Playlists);

public sealed record MergePlanEntry(string LocalKey, string Action, string PlaylistId, string? ServerName);

public sealed record MergePlanResponse(IReadOnlyList<MergePlanEntry> Plan);

public sealed record SyncCounts(long Likes, long Albums, long Artists, long Playlists, long Items, long Plays, long PlayedTracks);

public sealed record SyncSummary(string Cursor, string ServerTime, SyncCounts Counts);

public sealed record SyncRequest
{
    public required string Cursor { get; init; }
    public int? Limit { get; init; }
    public IReadOnlyList<string>? Streams { get; init; }
    public IReadOnlyList<JsonObject>? Ops { get; init; }
}

public sealed record OpResult(string OpId, string Status, string? Code, long? Seq, string? PlaylistId, int? RetryAfterSeconds, bool Replayed);

public sealed record PlaylistRow(string Id, string Name, string? BrowseId, string? ThumbnailUrl, string CreatedAt, bool Deleted);

public sealed record PlaylistItemRow(string PlaylistId, string VideoId, bool Present, string SortKey, string AddedAt);

public sealed record LikeRow(string VideoId, bool Liked, string? LikedAt);

public sealed record BookmarkRow(string Type, string BrowseId, bool Bookmarked, string? BookmarkedAt, string? Title, string? Subtitle, string? ThumbnailUrl, string? Year);

public sealed record PlayRow(string EventId, string VideoId, string PlayedAt, long PlayTimeMs, string? DeviceId);

public sealed record PlayStatRow(string VideoId, long TotalPlayTimeMs, string? LastPlayedAt);

public sealed record PlayForgetRow(string VideoId, string EventsBefore, string? TotalBefore);

public sealed record SyncResponse(
    IReadOnlyList<OpResult> Results,
    string Cursor,
    bool HasMore,
    string ServerTime,
    IReadOnlyList<TrackDto> Tracks,
    IReadOnlyList<PlaylistRow> Playlists,
    IReadOnlyList<PlaylistItemRow> Items,
    IReadOnlyList<LikeRow> Likes,
    IReadOnlyList<BookmarkRow> Bookmarks,
    IReadOnlyList<PlayRow> Plays,
    IReadOnlyList<PlayStatRow> PlayStats,
    IReadOnlyList<PlayForgetRow> PlayForgets);

// ---------- Тексты песен (API §4.10, docs/LYRICS-SYNC.md) ----------

/// <summary>Текст трека: обе стороны со своими источниками (<c>user|file|youtube_music|lrclib|kugou</c>).</summary>
public sealed record LyricsText(string? Plain, string? PlainSource, string? Synced, string? SyncedFormat, string? SyncedSource, long? StartTimeMs, string? Language);

/// <summary>Своя версия для <c>PUT</c>: нужен <c>plain</c> или <c>synced</c>; <c>syncedFormat</c> — вместе с <c>synced</c>.</summary>
public sealed record LyricsPut
{
    public string? Plain { get; init; }
    public string? PlainSource { get; init; }
    public string? Synced { get; init; }
    public string? SyncedFormat { get; init; }
    public string? SyncedSource { get; init; }
    public long? StartTimeMs { get; init; }
    public string? Language { get; init; }
}

public sealed record MyLyrics(string Id, string VideoId, long Rev, bool Deleted, LyricsText? Text, string UpdatedAt);

/// <summary>Общая версия другого пользователя; автор не раскрывается.</summary>
public sealed record SharedLyrics(string Id, string VideoId, LyricsText Text, string UpdatedAt);

public sealed record LyricsResponse(MyLyrics? Mine, SharedLyrics? Shared, string ServerTime);

public sealed record LyricsChangesRequest(long After, int? Limit = null);

public sealed record MyLyricsPage(IReadOnlyList<MyLyrics> Items, long Rev, bool More);

// ---------- Playback (API §4.9) ----------

public sealed record PlaybackHandoff(string DeviceId, string SessionId, string At);

public sealed record PlaybackHandoffInput(string DeviceId, string SessionId);

public sealed record PlaybackState(
    long Rev,
    string DeviceId,
    string? DeviceName,
    string SessionId,
    int QueueVersion,
    int Index,
    long PositionMs,
    long? DurationMs,
    bool Playing,
    string At,
    string UpdatedAt,
    IReadOnlyList<TrackDto> Queue,
    PlaybackHandoff? HandoffFrom);

public sealed record PlaybackStateResponse(PlaybackState? State, string ServerTime);

public sealed record PlaybackPut
{
    public required string SessionId { get; init; }
    public required int QueueVersion { get; init; }
    public required string At { get; init; }
    public required int Index { get; init; }
    public required long PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public required bool Playing { get; init; }
    public IReadOnlyList<TrackInput>? Queue { get; init; }
    public PlaybackHandoffInput? HandoffFrom { get; init; }
}

public sealed record PlaybackPutResult(bool Applied, long? Rev, string? Reason, PlaybackState? State, string ServerTime);

// ---------- SSE (API §6) ----------

public sealed record LiveEvent(string Id, string Type, string At, JsonObject? Payload);

// ---------- Ошибка (API §2.1) ----------

public sealed record ErrorEnvelope
{
    public int StatusCode { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public string? Code { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public int? DeviceLimit { get; init; }
    public int? DeviceCount { get; init; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; init; }
}
