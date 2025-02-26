﻿using System.Text.RegularExpressions;

using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;


namespace autotag.Core.Movie;
public class MovieProcessor : IProcessor, IDisposable
{
    private readonly TMDbClient _tmdb;
    private IEnumerable<Genre> _genres = Enumerable.Empty<Genre>();

    public MovieProcessor(string apiKey)
    {
        this._tmdb = new TMDbClient(apiKey);
    }

    public async Task<bool> ProcessAsync(
        TaggingFile file,
        Action<string> setPath,
        Action<string, MessageType> setStatus,
        Func<List<(string, string)>, int?> selectResult,
        AutoTagConfig config,
        FileWriter writer
    )
    {
        MovieFileMetadata result = new MovieFileMetadata();

        #region "Filename parsing"
        string pattern =
            "^((?<Title>.+?)[\\. _-]?)" + // get title by reading from start to a field (whichever field comes first)
            "?(" +
                "([\\(]?(?<Year>(19|20)[0-9]{2})[\\)]?)|" + // year - extract for use in searching
                "([0-9]{3,4}(p|i))|" + // resolution (e.g. 1080p, 720i)
                "((?:PPV\\.)?[HPS]DTV|[. ](?:HD)?CAM[| ]|B[DR]Rip|[.| ](?:HD-?)?TS[.| ]|(?:PPV )?WEB-?DL(?: DVDRip)?|HDRip|DVDRip|CamRip|W[EB]Rip|BluRay|DvDScr|hdtv|REMUX|3D|Half-(OU|SBS)+|4K|NF|AMZN)|" + // rip type
                "(xvid|[hx]\\.?26[45]|AVC)|" + // video codec
                "(MP3|DD5\\.?1|Dual[\\- ]Audio|LiNE|DTS[-HD]+|AAC[.-]LC|AAC(?:\\.?2\\.0)?|AC3(?:\\.5\\.1)?|7\\.1|DDP5.1)|" + // audio codec
                "(REPACK|INTERNAL|PROPER)|" + // scene tags
                "\\.(mp4|m4v|mkv)$" + // file extensions
            ")";

        Match match = Regex.Match(Path.GetFileName(file.Path), pattern);
        string title, year;
        if (match.Success)
        {
            title = match.Groups["Title"].ToString();
            year = match.Groups["Year"].ToString();
        }
        else
        {
            setStatus("Error: Failed to parse required information from filename", MessageType.Error);
            return false;
        }

        title = title.Replace('.', ' '); // change dots to spaces

        if (string.IsNullOrWhiteSpace(title))
        {
            setStatus("Error: Failed to parse required information from filename", MessageType.Error);
            return false;
        }

        if (config.Verbose)
        {
            setStatus($"Parsed file as {title}", MessageType.Information);
        }
        #endregion

        #region "TMDB API Searching"
        SearchContainer<SearchMovie> searchResults;
        if (!string.IsNullOrWhiteSpace(year))
        {
            searchResults = await _tmdb.SearchMovieAsync(query: title, year: int.Parse(year)); // if year was parsed, use it to narrow down search further
        }
        else
        {
            searchResults = await _tmdb.SearchMovieAsync(query: title);
        }

        int selected = 0;

        if (searchResults.Results.Count > 1 && (!searchResults.Results.First().Title.Equals(title, StringComparison.InvariantCultureIgnoreCase) || config.ManualMode))
        {
            int? selection = selectResult(
                searchResults.Results
                    .Select(m => (
                        m.Title,
                        m.ReleaseDate?.Year.ToString() ?? "Unknown"
                    )).ToList()
            );

            if (selection.HasValue)
            {
                selected = selection.Value;
            }
            else
            {
                setStatus("File skipped", MessageType.Warning);
                return true;
            }
        }
        else if (!searchResults.Results.Any())
        {
            setStatus($"Error: failed to find title {title} on TheMovieDB", MessageType.Error);
            result.Success = false;
            return false;
        }

        SearchMovie selectedResult = searchResults.Results[selected];

        setStatus($"Found {selectedResult.Title} ({selectedResult.ReleaseDate?.Year.ToString() ?? "unknown year"}) on TheMovieDB", MessageType.Information);
        #endregion

        result.Id = selectedResult.Id;

        result.Title = selectedResult.Title;
        result.Overview = selectedResult.Overview;
        result.CoverURL = string.IsNullOrEmpty(selectedResult.PosterPath) ? null : $"https://image.tmdb.org/t/p/original{selectedResult.PosterPath}";
        result.CoverFilename = selectedResult.PosterPath?.Replace("/", "");

        result.Date = selectedResult.ReleaseDate;

        if (!_genres.Any())
        {
            _genres = await _tmdb.GetMovieGenresAsync();
        }
        result.Genres = selectedResult.GenreIds.Select(gId => _genres.First(g => g.Id == gId).Name).ToList();

        if (config.ExtendedTagging && file.Taggable)
        {
            var credits = await _tmdb.GetMovieCreditsAsync(selectedResult.Id);

            result.Director = credits.Crew.FirstOrDefault(c => c.Job == "Director")?.Name;
            result.Actors = credits.Cast.Select(c => c.Name).ToList();
            result.Characters = credits.Cast.Select(c => c.Character).ToList();
        }

        if (String.IsNullOrEmpty(result.CoverURL))
        {
            setStatus("Error: failed to fetch movie cover", MessageType.Error);
            result.Complete = false;
        }

        bool taggingSuccess = await writer.WriteAsync(file, result, setPath, setStatus, config);

        return taggingSuccess && result.Success && result.Complete;
    }

    public void Dispose()
    {
        _tmdb.Dispose();
    }
}
