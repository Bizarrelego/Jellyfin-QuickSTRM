namespace QuickSTRM.Models;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// Strongly-typed JSON payload received from Radarr, Sonarr, and Whisparr webhooks.
/// </summary>
public class ArrWebhookPayload
{
    /// <summary>
    /// Gets or sets the webhook event type (e.g. Download, Rename, MovieFileDelete, EpisodeFileDelete, Test, Grab).
    /// </summary>
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    /// <summary>
    /// Gets or sets movie metadata for Radarr/Whisparr webhook events.
    /// </summary>
    [JsonPropertyName("movie")]
    public MovieInfo? Movie { get; set; }

    /// <summary>
    /// Gets or sets movie file metadata for Radarr/Whisparr webhook events.
    /// </summary>
    [JsonPropertyName("movieFile")]
    public FileInfo? MovieFile { get; set; }

    /// <summary>
    /// Gets or sets series metadata for Sonarr webhook events.
    /// </summary>
    [JsonPropertyName("series")]
    public SeriesInfo? Series { get; set; }

    /// <summary>
    /// Gets or sets episode metadata list for Sonarr webhook events.
    /// </summary>
    [JsonPropertyName("episodes")]
    public List<EpisodeInfo>? Episodes { get; set; }

    /// <summary>
    /// Gets or sets episode file metadata for Sonarr webhook events.
    /// </summary>
    [JsonPropertyName("episodeFile")]
    public FileInfo? EpisodeFile { get; set; }

    /// <summary>
    /// Nested class representing movie information (FolderPath, FilePath).
    /// </summary>
    public class MovieInfo
    {
        /// <summary>
        /// Gets or sets the destination folder path of the movie.
        /// </summary>
        [JsonPropertyName("folderPath")]
        public string? FolderPath { get; set; }

        /// <summary>
        /// Gets or sets the full file path of the movie file.
        /// </summary>
        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }
    }

    /// <summary>
    /// Nested class representing series information (Path).
    /// </summary>
    public class SeriesInfo
    {
        /// <summary>
        /// Gets or sets the root or destination path of the series.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }

    /// <summary>
    /// Nested class representing individual episode information (Path, SourcePath).
    /// </summary>
    public class EpisodeInfo
    {
        /// <summary>
        /// Gets or sets the destination file path for the episode.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        /// <summary>
        /// Gets or sets the original source file path for the episode.
        /// </summary>
        [JsonPropertyName("sourcePath")]
        public string? SourcePath { get; set; }
    }

    /// <summary>
    /// Nested class representing file-level information.
    /// </summary>
    public class FileInfo
    {
        /// <summary>
        /// Gets or sets the full file path.
        /// </summary>
        [JsonPropertyName("path")]
        public string? Path { get; set; }

        /// <summary>
        /// Gets or sets the relative file path.
        /// </summary>
        [JsonPropertyName("relativePath")]
        public string? RelativePath { get; set; }
    }
}

/// <summary>
/// Alias model for movie webhook payload data.
/// </summary>
public class Movie : ArrWebhookPayload.MovieInfo { }

/// <summary>
/// Alias model for series webhook payload data.
/// </summary>
public class Series : ArrWebhookPayload.SeriesInfo { }

/// <summary>
/// Alias model for episode webhook payload data.
/// </summary>
public class Episode : ArrWebhookPayload.EpisodeInfo { }
