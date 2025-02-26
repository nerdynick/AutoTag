﻿using System.Text.RegularExpressions;

namespace autotag.Core;
public abstract class FileMetadata
{
    public int Id;
    public string Title = null!;
    public string? Overview;
    public string? CoverURL;
    public string? CoverFilename;
    public bool Success;
    public bool Complete;
    public string? Director;
    public IEnumerable<string>? Actors;
    public IEnumerable<string>? Characters;
    public IEnumerable<string> Genres = null!;

    public FileMetadata()
    {
        Success = true;
        Complete = true;
    }

    public virtual void WriteToFile(TagLib.File file, AutoTagConfig config, Action<string, MessageType> setStatus)
    {
        file.Tag.Title = Title;
        file.Tag.Description = Overview;

        if (Genres != null && Genres.Any())
        {
            file.Tag.Genres = Genres.ToArray();
        }

        if (config.ExtendedTagging && file.MimeType == "video/x-matroska")
        {
            file.Tag.Conductor = Director;
            file.Tag.Performers = Actors?.ToArray();
            file.Tag.PerformersRole = Characters?.ToArray();
        }
    }

    public abstract string GetFileName(AutoTagConfig config);

    protected readonly static Regex _renameRegex = new Regex(@"%(?<num>\d+)(?:\:(?<format>[0#]+))?");

    protected static string FormatRenameNumber(Match match, int value)
    {
        if (match.Groups.ContainsKey("format"))
        {
            return value.ToString(match.Groups["format"].Value);
        }
        else
        {
            return value.ToString();
        }
    }

    public override abstract string ToString();
}